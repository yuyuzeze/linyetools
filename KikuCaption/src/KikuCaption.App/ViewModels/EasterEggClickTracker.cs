namespace KikuCaption.App.ViewModels;

/// <summary>Recognizes five brand clicks inside a short, uninterrupted interval.</summary>
public sealed class EasterEggClickTracker
{
    private readonly int _requiredClicks;
    private readonly TimeSpan _maximumGap;
    private int _clickCount;
    private DateTimeOffset _lastClick;

    public EasterEggClickTracker(int requiredClicks = 5, TimeSpan? maximumGap = null)
    {
        _requiredClicks = requiredClicks;
        _maximumGap = maximumGap ?? TimeSpan.FromSeconds(2);
    }

    public bool Register(DateTimeOffset now)
    {
        _clickCount = _clickCount > 0 && now - _lastClick <= _maximumGap
            ? _clickCount + 1
            : 1;
        _lastClick = now;

        if (_clickCount < _requiredClicks)
        {
            return false;
        }

        _clickCount = 0;
        return true;
    }
}
