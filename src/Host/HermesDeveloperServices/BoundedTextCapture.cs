using System.Text;

namespace HermesDeveloperServices;

internal sealed class SharedCharacterBudget
{
    private readonly object _gate = new();
    private int _remaining;
    private long _dropped;

    public SharedCharacterBudget(int maximumCharacters)
    {
        _remaining = maximumCharacters;
    }

    public long Dropped
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    public int Reserve(int requested)
    {
        lock (_gate)
        {
            var granted = Math.Min(requested, _remaining);
            _remaining -= granted;
            _dropped += requested - granted;
            return granted;
        }
    }
}

internal sealed class BoundedTextCapture
{
    private readonly object _gate = new();
    private readonly SharedCharacterBudget _budget;
    private readonly StringBuilder _builder = new();

    public BoundedTextCapture(SharedCharacterBudget budget)
    {
        _budget = budget;
    }

    public void AppendLine(string line)
    {
        var value = line + Environment.NewLine;
        var retained = _budget.Reserve(value.Length);
        if (retained == 0)
        {
            return;
        }

        lock (_gate)
        {
            _builder.Append(value, 0, retained);
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return _builder.ToString();
        }
    }
}
