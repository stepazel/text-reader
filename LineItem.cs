using System.ComponentModel;

namespace TextReader;

public class LineItem : INotifyPropertyChanged
{
    private string _query = "";

    public string Text { get; }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public LineItem(string text, string query = "")
    {
        Text = text;
        _query = query;
    }
}
