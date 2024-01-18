/*
    Copyright 2023 Carl Foghammar Nömtak

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/

using Microsoft;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using VSIntelliSenseTweaks.Utilities;

using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using VSCompletionItem = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data.CompletionItem;
using Microsoft.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace VSIntelliSenseTweaks
{
    // TODO: How to make a user setting that stops the MEF export of this?
    [Export(typeof(IAsyncCompletionItemManagerProvider))]
    [Name(nameof(VSIntelliSenseTweaksCompletionItemManagerProvider))]
    [ContentType("CSharp")]
    [ContentType("CSS")]
    [ContentType("XAML")]
    [ContentType("XML")]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal class VSIntelliSenseTweaksCompletionItemManagerProvider : IAsyncCompletionItemManagerProvider
    {
        public IAsyncCompletionItemManager GetOrCreate(ITextView textView)
        {
            VSIntelliSenseTweaksPackage.EnsurePackageLoaded();
            var settings = VSIntelliSenseTweaksPackage.Settings;
            return new CompletionItemManager(settings);
        }
    }

    internal class CompletionItemManager : IAsyncCompletionItemManager2
    {
        static readonly ImmutableArray<Span> noSpans = ImmutableArray<Span>.Empty;
        
        const int textFilterMaxLength = 256;

        IAsyncCompletionSession session;
        AsyncCompletionSessionInitialDataSnapshot initialData;
        AsyncCompletionSessionDataSnapshot currentData;
        CancellationToken cancellationToken;

        VSCompletionItem[] completions;
        CompletionItemKey[] keys;
        int n_completions;

        WordScorer scorer = new WordScorer(256);

        CompletionFilterManager filterManager;
        bool hasFilterManager;

        bool includeDebugSuffix;
        bool disableSoftSelection;
        bool boostEnumMemberScore;

        public CompletionItemManager(GeneralSettings settings)
        {
            this.includeDebugSuffix = settings.IncludeDebugSuffix;
            this.disableSoftSelection = settings.DisableSoftSelection;
            this.boostEnumMemberScore = settings.BoostEnumMemberScore;
        }

        public Task<ImmutableArray<VSCompletionItem>> SortCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            // I think this method is not used, but required for the interface.
            throw new NotImplementedException();
        }

        public Task<CompletionList<VSCompletionItem>> SortCompletionItemListAsync(IAsyncCompletionSession session, AsyncCompletionSessionInitialDataSnapshot data, CancellationToken token)
        {
            // Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Implementation.AsyncCompletionSession
            this.session = session;
            this.initialData = data;
            this.cancellationToken = token;

            var sortTask = Task.Factory.StartNew(SortCompletionList, token, TaskCreationOptions.None, TaskScheduler.Current);

            return sortTask;
        }

        public Task<FilteredCompletionModel> UpdateCompletionListAsync(IAsyncCompletionSession session, AsyncCompletionSessionDataSnapshot data, CancellationToken token)
        {
            Debug.Assert(this.session == session);
            Debug.Assert(this.cancellationToken == token);
            this.currentData = data;

            var updateTask = Task.Factory.StartNew(UpdateCompletionList, token, TaskCreationOptions.None, TaskScheduler.Current);

            return updateTask;
        }

        public CompletionList<VSCompletionItem> SortCompletionList()
        {
            using (new Measurement(nameof(SortCompletionList)))
            {
                var initialCompletions = initialData.InitialItemList;
                this.n_completions = initialCompletions.Count;

                // Debug.WriteLine($"Allocating for {n_completions} completions");

                this.completions = new VSCompletionItem[n_completions];
                this.keys = new CompletionItemKey[n_completions];
                this.hasFilterManager = false;

                for (int i = 0; i < n_completions; i++)
                {
                    completions[i] = initialCompletions[i];
                }

                using (new Measurement("Sort"))
                Array.Sort(completions, new InitialComparer());

                using (new Measurement(nameof(session.CreateCompletionList)))
                {
                    var completionList = session.CreateCompletionList(completions);

                    return completionList;
                }
            }
        }

		static unsafe int FastUnsafeParse ( ReadOnlySpan<char> number )
		{
			fixed ( char * startPtr = number )
			{
				var length = number.Length;
				char* ptr = startPtr;
				if ( length == 1 )
				{
					return *ptr - '0';
				}
				else if ( length == 2 )
				{
					int result = (*ptr - '0') * 10;
					ptr++;
					result += *ptr - '0';
					return result;
				}
			}

			throw new ArgumentException ( "Invalid input" );
		}

		static readonly Dictionary<string, SymbolKind> completionItemSymbolKindCache = new Dictionary<string, SymbolKind>();

		[MethodImpl ( MethodImplOptions.AggressiveInlining )]
		static bool TryGetCompletionSymbolKind ( VSCompletionItem completion, out SymbolKind kind )
		{
			if ( completionItemSymbolKindCache.TryGetValue ( completion.FilterText, out kind ) )
				return true;

			if ( completion.Properties.TryGetProperty ( "RoslynCompletionItemData", out object roslynObject ) )
			{
				var roslynCompletion = GetRoslynItemProperty(roslynObject);
				if ( roslynCompletion.Properties.TryGetValue ( "SymbolKind", out var value ) )
				{
					kind = (SymbolKind)FastUnsafeParse ( value.AsSpan () );
					completionItemSymbolKindCache.Add ( completion.FilterText, kind );
					// Debug.WriteLine ( $"Adding {completion.DisplayText}" );
					return true;
				}
			}

			kind = SymbolKind.Alias;
			return false;
		}

		public enum StartsWithResult
		{
			ExactMatch,
			IgnoreCaseMatch,
			NoMatch
		}

		/*
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static StartsWithResult StartsWithSlow ( ReadOnlySpan<char> number, ReadOnlySpan<char> sub )
		{
			if ( sub.Length > number.Length )
				return StartsWithResult.NoMatch;

			if ( number.StartsWith ( sub ) )
				return StartsWithResult.ExactMatch;

			if ( number.StartsWith ( sub, StringComparison.OrdinalIgnoreCase ) )
				return StartsWithResult.IgnoreCaseMatch;

			return StartsWithResult.NoMatch;
		}
		*/

		[MethodImpl ( MethodImplOptions.AggressiveInlining )]
		public static StartsWithResult StartsWith ( ReadOnlySpan<char> word, ReadOnlySpan<char> sub )
		{
			unsafe
			{
				var subLength = sub.Length;
			
				if ( subLength > word.Length )
					return StartsWithResult.NoMatch;

				var caseMatching = true;
			
				fixed ( char* fixedWord = word )
				fixed ( char* fixedSub = sub )
				{
					var wordPtr = fixedWord;
					var subPtr = fixedSub;
					for ( int i = 0; i < subLength; i++ )
					{
						var comp = *wordPtr - * subPtr;
						wordPtr++;
						subPtr++;

						switch ( comp )
						{
							case 32:
							case -32:
								caseMatching = false;
								continue;
							case 0:
								continue;
						}

						return StartsWithResult.NoMatch;
					}
				}

				return caseMatching ? StartsWithResult.ExactMatch : StartsWithResult.IgnoreCaseMatch;
			}
		}

		public FilteredCompletionModel UpdateCompletionList()
        {
            using (new Measurement(nameof(UpdateCompletionList)))
			{
				var textFilter = session.ApplicableToSpan.GetText(currentData.Snapshot);
                bool hasTextFilter = textFilter.Length > 0;

                if (ShouldDismiss()) return null;

                var filterStates = currentData.SelectedFilters; // The types of filters displayed in the IntelliSense widget.
                if (!hasFilterManager)
                {
                    this.filterManager = new CompletionFilterManager(filterStates);
                    this.hasFilterManager = true;
                }
                filterManager.UpdateActiveFilters(filterStates);

                int n_eligibleCompletions = 0;
                using (new Measurement(nameof(DetermineEligibleCompletions)))
					DetermineEligibleCompletions();

                var highlighted = CreateHighlightedCompletions(n_eligibleCompletions);
                var selectionKind = GetSelectionKind(n_eligibleCompletions, hasTextFilter);

                var result = new FilteredCompletionModel
                (
                    items: highlighted,
                    selectedItemIndex: 0,
                    filters: filterStates,
                    selectionHint: selectionKind,
                    centerSelection: false,
                    uniqueItem: null
                );

                Debug.Assert(!cancellationToken.IsCancellationRequested);

                return result;

                bool ShouldDismiss()
                {
                    // Dismisses if first char in pattern is a number and not after a '.'.
                    int position = session.ApplicableToSpan.GetStartPoint(currentData.Snapshot).Position;
                    return hasTextFilter
                        && char.IsNumber(currentData.Snapshot[position])
                        && position > 0 && currentData.Snapshot[position - 1] != '.';
                }

				void DetermineEligibleCompletions()
                {
                    var initialCompletions = currentData.InitialSortedItemList;
                    var defaults = currentData.Defaults;
                    Debug.Assert(n_completions == initialCompletions.Count);

                    int patternLength = Math.Min(textFilter.Length, textFilterMaxLength);
                    var pattern = textFilter.AsSpan(0, patternLength);

					
                    ReadOnlySpan<char> roslynPreselectedItemFilterText = null;
                    BitField64 availableFilters = default;
                    for (int i = 0; i < n_completions; i++)
                    {
                        var completion = initialCompletions[i];

                        int patternScore;
                        ImmutableArray<Span> matchedSpans;
                        if (hasTextFilter)
                        {
                            var word = completion.FilterText.AsSpan();
							var textAsSpan = textFilter.AsSpan();
							var comp = StartsWith ( word, textAsSpan );
							if ( comp != StartsWithResult.NoMatch )
							{
								patternScore = 1000;
								matchedSpans = noSpans;

								if ( comp == StartsWithResult.ExactMatch )
									patternScore += 200;

								if ( word.Length == textAsSpan.Length )
								{
									patternScore += 1000;
								}
								else
								{
									patternScore -= word.Length;
									if ( TryGetCompletionSymbolKind ( completion, out var symbolKind ) )
									{
										switch ( symbolKind )
										{
											case SymbolKind.Local:
												patternScore += 600;
												break;
											case SymbolKind.Parameter:
												patternScore += 500;
												break;
											case SymbolKind.Field:
											case SymbolKind.Property:
												patternScore += 400;
												break;
											case SymbolKind.Method:
												patternScore += 200;
												break;
										}
									}
								}
							}
							else
							{
								int displayTextOffset = Math.Max(0, completion.DisplayText.AsSpan().IndexOf(word));
								patternScore = scorer.ScoreWord(word, pattern, displayTextOffset, out matchedSpans);
								if (patternScore == int.MinValue) continue;
							}
                        }
                        else
                        {
                            patternScore = int.MinValue;
                            matchedSpans = noSpans;
                        }

                        var filterMask = filterManager.CreateFilterMask(completion.Filters);
                        var blacklistFilters = filterManager.blacklist & filterMask;
                        availableFilters |= blacklistFilters; // Announce available blacklist filters.
                        if (filterManager.HasActiveBlacklistFilter(filterMask)) continue;

                        var whitelistFilters = filterManager.whitelist & filterMask;
                        availableFilters |= whitelistFilters; // Announce available whitelist filters.
                        if (filterManager.HasActiveWhitelist && !filterManager.HasActiveWhitelistFilter(filterMask)) continue;

                        int defaultIndex = defaults.IndexOf(completion.FilterText) & int.MaxValue; // AND operation turns any negative value to int.MaxValue so we can sort properly

                        if (blacklistFilters != default)
                        {
                            // We penalize items that have any inactive blacklist filters.
                            // The current filter settings allow these items to be shown but they should be of lesser value than items without any blacklist filters.
                            // Currently the only type of blacklist filter that exist in VS is 'add items from unimported namespaces'.
                            patternScore -= 64 * pattern.Length;
                        }

                        int roslynScore = boostEnumMemberScore ?
                            GetBoostedRoslynScore(completion, ref roslynPreselectedItemFilterText) :
                            GetRoslynScore(completion);

                        patternScore += CalculateRoslynScoreBonus(roslynScore, pattern.Length);

						var key = new CompletionItemKey
                        {
                            patternScore = patternScore,
                            defaultIndex = defaultIndex,
                            roslynScore = roslynScore,
                            initialIndex = i,
                            matchedSpans = matchedSpans,
                        };

                        if (this.includeDebugSuffix)
                        {
                            AddDebugSuffix(ref completion, in key);
                        }

                        this.completions[n_eligibleCompletions] = completion;
                        this.keys[n_eligibleCompletions] = key;
                        n_eligibleCompletions++;
                    }

					using (new Measurement("Sort"))
						Array.Sort(keys, completions, 0, n_eligibleCompletions);
					
					filterStates = UpdateFilterStates(filterStates, availableFilters);
                }
			}
        }

        UpdateSelectionHint GetSelectionKind(int n_eligibleCompletions, bool hasTextFilter)
        {
            if (n_eligibleCompletions == 0)
                return UpdateSelectionHint.NoChange;

            if (IsSoftSelectionDisabled())
                return UpdateSelectionHint.Selected;

            if (hasTextFilter && !currentData.DisplaySuggestionItem)
                return UpdateSelectionHint.Selected;

            var bestKey = keys[0];

            if (bestKey.roslynScore >= MatchPriority.Preselect)
                return UpdateSelectionHint.Selected;

            //if (bestKey.defaultIndex < int.MaxValue)
            //    return UpdateSelectionHint.Selected;

            return UpdateSelectionHint.SoftSelected;

            bool IsSoftSelectionDisabled()
            {
                // User setting to disable soft-selection.
                if (disableSoftSelection)
                {
                    // If the user prefers hard-selection, we can disable soft-selection under the following circumstances.
                    return currentData.InitialTrigger.Reason == CompletionTriggerReason.InvokeAndCommitIfUnique
                        || currentData.InitialTrigger.Character.Equals('.');
                }
                return false;
            }
        }

        ImmutableArray<CompletionItemWithHighlight> CreateHighlightedCompletions(int n_eligibleCompletions)
        {
            var builder = ImmutableArray.CreateBuilder<CompletionItemWithHighlight>(n_eligibleCompletions);
            builder.Count = n_eligibleCompletions;
            for (int i = 0; i < n_eligibleCompletions; i++)
            {
                builder[i] = new CompletionItemWithHighlight(completions[i], keys[i].matchedSpans);
            }
            return builder.MoveToImmutable();
        }

        private int CalculateRoslynScoreBonus(int roslynScore, int patternLength)
        {
            const int roslynScoreClamper = 1 << 22;
            int clampedRoslynScore = Math.Max(Math.Min(roslynScore, roslynScoreClamper), -roslynScoreClamper);
            return clampedRoslynScore * patternLength / 64;
        }

        /// <summary>
        /// Returns the normal roslyn score but gives additional score to enum members if the enum type was preselected by roslyn.
        /// </summary>
        private int GetBoostedRoslynScore(VSCompletionItem completion, ref ReadOnlySpan<char> roslynPreselectedItemFilterText)
        {
            int roslynScore = GetRoslynScore(completion);

            if (roslynScore >= MatchPriority.Preselect)
            {
                roslynPreselectedItemFilterText = completion.DisplayText.AsSpan();
            }
            else if (roslynPreselectedItemFilterText != null)
            {
                var word = completion.DisplayText.AsSpan();
                int preselectedLength = roslynPreselectedItemFilterText.Length;

                if (word.Length > preselectedLength
                &&  word.Slice(0, preselectedLength).SequenceEqual(roslynPreselectedItemFilterText))
                {
                    if (word[preselectedLength] == '.')
                    {
                        roslynScore = MatchPriority.Preselect / 2;
                    }
                }
                else
                {
                    roslynPreselectedItemFilterText = null;
                }
            }

            return roslynScore;
        }

        private int GetRoslynScore(VSCompletionItem completion)
        {
            if (completion.Properties.TryGetProperty("RoslynCompletionItemData", out object roslynObject))
            {
                var roslynCompletion = GetRoslynItemProperty(roslynObject);
                int roslynScore = roslynCompletion.Rules.MatchPriority;
                return roslynScore;
            }

            return 0;
        }

        // Since we do not have compile time access the class type;
        // "Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.AsyncCompletion.CompletionItemData",
        // we have to use reflection or expressions to access it.
        private static Func<object, RoslynCompletionItem> RoslynCompletionItemGetter = null;

        private static RoslynCompletionItem GetRoslynItemProperty(object roslynObject)
        {
			if (RoslynCompletionItemGetter == null)
            {
                // Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.AsyncCompletion.CompletionItemData
                var roslynType = roslynObject.GetType();
                var input    = Expression.Parameter(typeof(object));
                var casted   = Expression.Convert(input, roslynType);
                var property = Expression.PropertyOrField(casted, "RoslynItem");
                var lambda   = Expression.Lambda(property, input);
                RoslynCompletionItemGetter = (Func<object, RoslynCompletionItem>)lambda.Compile();
            }

            return RoslynCompletionItemGetter.Invoke(roslynObject);
        }

        private ImmutableArray<CompletionFilterWithState> UpdateFilterStates(ImmutableArray<CompletionFilterWithState> filterStates, BitField64 availableFilters)
        {
            int n_filterStates = filterStates.Length;
            var builder = ImmutableArray.CreateBuilder<CompletionFilterWithState>(n_filterStates);
            builder.Count = n_filterStates;
            for (int i = 0; i < n_filterStates; i++)
            {
                var filterState = filterStates[i];
                builder[i] = new CompletionFilterWithState(filterState.Filter, availableFilters.GetBit(i), filterState.IsSelected);
            }
            return builder.MoveToImmutable();
        }

        struct InitialComparer : IComparer<VSCompletionItem>
        {
            public int Compare(VSCompletionItem x, VSCompletionItem y)
            {
                var a = x.SortText.AsSpan();
                var b = y.SortText.AsSpan();

                int comp = 0;
                if (a.Length > 0 && b.Length > 0)
                {
                    comp = GetUnderscoreCount(a) - GetUnderscoreCount(b);
                }
                if (comp == 0)
                {
                    comp = a.SequenceCompareTo(b);
                }
                return comp;
            }

            private int GetUnderscoreCount(ReadOnlySpan<char> str)
            {
                int i = 0;
                while (i < str.Length && str[i] == '_')
                {
                    i++;
                }
                return i;
            }
        }

        struct CompletionItemKey : IComparable<CompletionItemKey>
        {
            public int patternScore;
            public int defaultIndex;
            public int roslynScore;
            public int initialIndex;
            public ImmutableArray<Span> matchedSpans;

            public int CompareTo(CompletionItemKey other)
            {
                int comp = other.patternScore - patternScore;
                if (comp == 0)
                {
                    comp = defaultIndex - other.defaultIndex;
                }
                if (comp == 0)
                {
                    comp = other.roslynScore - roslynScore;
                }
                if (comp == 0) // If score is same, preserve initial ordering.
                {
                    comp = initialIndex - other.initialIndex;
                }
                return comp;
            }
        }

        struct CompletionFilterManager
        {
            CompletionFilter[] filters;
            public readonly BitField64 blacklist;
            public readonly BitField64 whitelist;
            BitField64 activeBlacklist;
            BitField64 activeWhitelist;

            /// <summary>
            /// True when there is an active whitelist to perform checks against.
            /// </summary>
            public bool HasActiveWhitelist => activeWhitelist != default;

            enum CompletionFilterKind
            {
                Null, Blacklist, Whitelist
            }

            public CompletionFilterManager(ImmutableArray<CompletionFilterWithState> filterStates)
            {
                Assumes.True(filterStates.Length < 64);

                filters = new CompletionFilter[filterStates.Length];
                blacklist = default;
                whitelist = default;
                activeBlacklist = default;
                activeWhitelist = default;
                

                for (int i = 0; i < filterStates.Length; i++)
                {
                    var filterState = filterStates[i];
                    var filter = filterState.Filter;
                    this.filters[i] = filter;
                    var filterKind = GetFilterKind(i, filter);
                    switch (filterKind)
                    {
                        case CompletionFilterKind.Blacklist:
                            blacklist.SetBit(i);
                            break;

                        case CompletionFilterKind.Whitelist:
                            whitelist.SetBit(i);
                            break;

                        default: throw new Exception();
                    }
                }

                CompletionFilterKind GetFilterKind(int index, CompletionFilter filter)
                {
                    // Is there a safer rule to determine what kind of filter it is?
                    return index == 0 ? CompletionFilterKind.Blacklist : CompletionFilterKind.Whitelist;
                }
            }

            public void UpdateActiveFilters(ImmutableArray<CompletionFilterWithState> filterStates)
            {
                Debug.Assert(filterStates.Length == filters.Length);

                BitField64 selection = default;
                for (int i = 0; i < filterStates.Length; i++)
                {
                    if (filterStates[i].IsSelected)
                    {
                        selection.SetBit(i);
                    }
                }

                activeBlacklist = ~selection & blacklist;
                activeWhitelist =  selection & whitelist;
            }

            public BitField64 CreateFilterMask(ImmutableArray<CompletionFilter> completionFilters)
            {
                BitField64 mask = default;
                for (int i = 0; i < completionFilters.Length; i++)
                {
                    int index = Array.IndexOf(filters, completionFilters[i]);
                    Debug.Assert(index >= 0);
                    mask.SetBit(index);
                }
                return mask;
            }

            public bool HasActiveBlacklistFilter(BitField64 completionFilters)
            {
                bool isOnBlacklist = (activeBlacklist & completionFilters) != default;
                return isOnBlacklist;
            }

            public bool HasActiveWhitelistFilter(BitField64 completionFilters)
            {
                Debug.Assert(HasActiveWhitelist);
                bool isOnWhitelist = (activeWhitelist & completionFilters) != default;
                return isOnWhitelist;
            }

            public bool PassesActiveFilters(BitField64 completionFilters)
            {
                return !HasActiveBlacklistFilter(completionFilters) && HasActiveWhitelistFilter(completionFilters);
            }
        }

        private void AddDebugSuffix(ref VSCompletionItem completion, in CompletionItemKey key)
        {
            var patternScoreString = key.patternScore == int.MinValue ? "-" : key.patternScore.ToString();
            var defaultIndexString = key.defaultIndex == int.MaxValue ? "-" : key.defaultIndex.ToString();
            var roslynScoreString = key.roslynScore == 0 ? "-" : key.roslynScore.ToString();

            var debugSuffix = $" (pattScr: {patternScoreString}, dfltIdx: {defaultIndexString}, roslScr: {roslynScoreString}, initIdx: {key.initialIndex})";
            
            var modifiedCompletion = new VSCompletionItem
            (
                completion.DisplayText,
                completion.Source,
                completion.Icon,
                completion.Filters,
                completion.Suffix + debugSuffix,
                completion.InsertText,
                completion.SortText,
                completion.FilterText,
                completion.AutomationText,
                completion.AttributeIcons,
                completion.CommitCharacters,
                completion.ApplicableToSpan,
                completion.IsCommittedAsSnippet,
                completion.IsPreselected
            );

            foreach (var property in completion.Properties.PropertyList)
            {
                modifiedCompletion.Properties.AddProperty(property.Key, property.Value);
            }

            completion = modifiedCompletion;
        }
    }
}