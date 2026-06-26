using System.ComponentModel;

namespace TextReader;

public class LineItem : INotifyPropertyChanged
{
    private string _query = "";
    private int _currentOccurrenceIndex = -1;

    public string Text { get; }
    public string LineNumberText { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value) return;
            _query = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
        }
    }

    // -1 = no current occurrence on this line; >=0 = which occurrence (0-based) is the current result
    public int CurrentOccurrenceIndex
    {
        get => _currentOccurrenceIndex;
        set
        {
            if (_currentOccurrenceIndex == value) return;
            _currentOccurrenceIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentOccurrenceIndex)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LineItem(string text, string query, long lineNumber)
    {
        Text = text;
        _query = query;
        LineNumberText = (lineNumber + 1).ToString();
    }
}
