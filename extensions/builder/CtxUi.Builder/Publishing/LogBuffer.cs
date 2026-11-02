using System.Text;

namespace CtxUi.Builder.Publishing;

public sealed class LogBuffer
{
    public const int MaxLines = 2000;
    public const int MaxLineLength = 4000;

    private readonly List<string> _lines = [];
    private readonly StringBuilder _partial = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
                return _partial.Length == 0 ? [.. _lines] : [.. _lines, _partial.ToString()];
        }
    }

    public void Append(string chunk)
    {
        lock (_gate)
        {
            foreach (var character in chunk)
            {
                if (character == '\r')
                    continue;
                if (character == '\n')
                {
                    Commit();
                    continue;
                }

                _partial.Append(character);
                if (_partial.Length >= MaxLineLength)
                    Commit();
            }

            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
    }

    private void Commit()
    {
        _lines.Add(_partial.ToString());
        _partial.Clear();
    }
}
