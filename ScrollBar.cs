// using Avalonia.Controls;
//
// namespace TextReader;
//
// public class ScrollBar(TextBlock[] textBlocks, TextProvider provider, double lineHeight)
// {
//     void OnScroll(long firstLine)
//     {
//         for (int i = 0; i < textBlocks.Length; i++)
//         {
//             textBlocks[i].Text = provider.GetLine(firstLine + i);
//             Canvas.SetTop(textBlocks[i], i * lineHeight);
//         }
//     }
//
//
// }