using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace YouTubeChatter
{
  public partial class MainWindow : Window
  {
    private readonly YouTubeChatExtractor _chatExtractor = new();
    private List<Line> _allChatLines = new();
    private CancellationTokenSource _cts;
    private readonly object _sync = new object();

    public MainWindow()
    {
      InitializeComponent();
      txtUrl.KeyDown += TxtUrl_KeyDown;
      txtSearch.TextChanged += TxtSearch_TextChanged;

      _chatExtractor.OnProgress = (newMessages) =>
      {
        Dispatcher.Invoke(() =>
        {
          lock (_sync)
          {
            _allChatLines.AddRange(newMessages);
            var term = txtSearch.Text.Trim();
            var lineAdded = false;
            foreach (var line in newMessages)
            {
              if (LineMatchesSearch(term, line))
              {
                txtResults.AppendText($"{Environment.NewLine}{line}");
                lineAdded = true;
              }
            }

            if (lineAdded)
              txtResults.ScrollToEnd();
          }
        });
      };

      _chatExtractor.OnCaughtUp = () =>
      {
        Dispatcher.Invoke(() =>
        {
          // Enable only after caught up to present
          dckReply.IsEnabled = true;
          txtSearch.IsEnabled = true;
        });
      };

      _chatExtractor.OnLiveStarted = () =>
      {
        Dispatcher.Invoke(() =>
        {
          lblIsLive.Visibility = Visibility.Visible;
          lblComplete.Visibility = Visibility.Collapsed;
        });
      };

      _chatExtractor.OnLiveEnded = () =>
      {
        Dispatcher.Invoke(() =>
        {
          lblIsLive.Visibility = Visibility.Collapsed;
          lblComplete.Visibility = Visibility.Visible;
        });
      };
    }

    private async void TxtUrl_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key != Key.Enter)
        return;

      var url = txtUrl.Text.Trim();
      if (string.IsNullOrEmpty(url))
        return;

      StartNewDownload();

      _cts?.Cancel();
      _cts = new CancellationTokenSource();

      try
      {
        _allChatLines = await _chatExtractor.DownloadChatAsync(url, _cts.Token);
      }
      catch (OperationCanceledException)
      {
        // Cancelled, fine
      }
      catch (Exception ex)
      {
        MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
      }
    }

    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
      lock(_sync)
        ApplySearch();
    }

    private void StartNewDownload()
    {
      txtResults.Clear();
      txtSearch.Clear();
      txtSearch.IsEnabled = false;
      dckReply.IsEnabled = false;
      _allChatLines.Clear();
      lblIsLive.Visibility = Visibility.Collapsed;
      lblComplete.Visibility = Visibility.Collapsed;
    }

    private bool LineMatchesSearch(string term, Line line)
    {
      if (term != string.Empty)
        return line.Message.Contains(term, StringComparison.OrdinalIgnoreCase) || line.TimeAuthor.Contains(term, StringComparison.OrdinalIgnoreCase);
      else
        return true;
    }

    private void ApplySearch()
    {
      string term = txtSearch.Text.Trim();
      if (string.IsNullOrEmpty(term))
      {
        ShowAllChat();
        return;
      }

      var filtered = _allChatLines.FindAll(l => LineMatchesSearch(term, l));
      txtResults.Text = string.Join(Environment.NewLine, filtered);
    }

    private void ShowAllChat()
    {
      txtResults.Text = string.Join(Environment.NewLine, _allChatLines);
    }
  }
}