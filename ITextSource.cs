using System;
using System.Threading.Tasks;

namespace TextReader;


public interface ITextSource
{
    long LineCount { get; }
    string GetLine(long index);
    Task LoadAsync(IProgress<double> progress);  // indexing / downloading
}
