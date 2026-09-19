using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ModProfiler.UI
{
    internal sealed class JsonBuilder
    {
        private readonly StringBuilder sb;
        private readonly Stack<bool> first = new Stack<bool>();

        internal JsonBuilder(StringBuilder sb)
        {
            this.sb = sb;
        }

        internal JsonBuilder BeginObject()
        {
            Separate();
            sb.Append('{');
            first.Push(true);
            return this;
        }

        internal JsonBuilder BeginObject(string name)
        {
            Key(name);
            sb.Append('{');
            first.Push(true);
            return this;
        }

        internal JsonBuilder EndObject()
        {
            sb.Append('}');
            first.Pop();
            return this;
        }

        internal JsonBuilder BeginArray(string name)
        {
            Key(name);
            sb.Append('[');
            first.Push(true);
            return this;
        }

        internal JsonBuilder EndArray()
        {
            sb.Append(']');
            first.Pop();
            return this;
        }

        internal JsonBuilder Value(string name, string value)
        {
            Key(name);
            AppendString(sb, value);
            return this;
        }

        internal JsonBuilder Value(string name, long value)
        {
            Key(name);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        internal JsonBuilder Value(string name, bool value)
        {
            Key(name);
            sb.Append(value ? "true" : "false");
            return this;
        }

        internal JsonBuilder Value(string name, double value, int decimals)
        {
            Key(name);
            AppendNumber(sb, value, decimals);
            return this;
        }
        internal JsonBuilder ValueOrNull(string name, double value, int decimals)
        {
            Key(name);
            if (double.IsNaN(value) || double.IsInfinity(value))
                sb.Append("null");
            else
                AppendNumber(sb, value, decimals);
            return this;
        }

        private void Key(string name)
        {
            Separate();
            AppendString(sb, name);
            sb.Append(':');
        }

        private void Separate()
        {
            if (first.Count == 0)
                return;
            if (first.Peek())
            {
                first.Pop();
                first.Push(false);
            }
            else
                sb.Append(',');
        }

        internal static void AppendNumber(StringBuilder sb, double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = 0.0;
            sb.Append(value.ToString("F" + decimals, CultureInfo.InvariantCulture));
        }

        internal static void AppendString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            foreach (char c in value)
            {
                if (c == '"' || c == '\\')
                    sb.Append('\\').Append(c);
                else if (c < ' ')
                    sb.Append("\\u").Append(((int)c).ToString("x4"));
                else
                    sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
