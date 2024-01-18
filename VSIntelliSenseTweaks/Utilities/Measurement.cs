// #if DEBUG
#define MEASURE_TIME
// #endif

using System;
using System.Text;
using System.Diagnostics;

namespace VSIntelliSenseTweaks.Utilities
{
    struct Measurement : IDisposable
    {
#if MEASURE_TIME
        static int depth = 0;
        static StringBuilder builder = new StringBuilder();
        static int backupLength = -1;

        Stopwatch watch;
        int insertPos;
#endif
        public Measurement(string name) : this()
        {
#if MEASURE_TIME
            builder.AppendLine();
            for (int i = 0; i < depth; i++)
            {
                builder.Append("|   ");
            }
            builder.Append("'");
            builder.Append(name);
			builder.Append ( ' ' );
            insertPos = builder.Length;
            backupLength = builder.Length;
			builder.AppendLine ( " ms" );
            for (int i = 0; i < depth; i++)
            {
                builder.Append("|   ");
            }
            builder.Append("{");
            depth++;
            watch = Stopwatch.StartNew();
#endif
        }

        public void Dispose()
        {
#if MEASURE_TIME
            var ms = (double)watch.ElapsedTicks * 1000 / Stopwatch.Frequency;
            depth--;
            if (backupLength >= 0)
            {
                builder.Length = backupLength;
                backupLength = -1;
            }
            else
            {
                builder.AppendLine();
                for (int i = 0; i < depth; i++)
                {
                    builder.Append("|   ");
                }
                builder.Append("}");
            }
            builder.Insert(insertPos, ms.ToString("0.00"));

            if (depth == 0)
            {
                Debug.WriteLine(builder.ToString());
                builder.Clear();
            }
#endif
        }
    }
}