using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FileSearchApp
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource _cancellationTokenSource;
        private readonly List<FileSystemItemViewModel> _foundFiles;
        private readonly object _lock = new object();
        private readonly Stopwatch _stopwatch;
        private readonly DispatcherTimer _timer;
        public event EventHandler<FileSystemItemEventArgs> FileFound;
        public event EventHandler<DirectoryChangedEventArgs> DirectoryChanged;
        private string _cacheFilePath;
        private int _totalFilesChecked;
        private int _matchedFilesCount;
        public MainWindow()
        {
            InitializeComponent();
            _foundFiles = new List<FileSystemItemViewModel>();
            _stopwatch = new Stopwatch();
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            FileFound += MainWindow_FileFound;
            DirectoryChanged += MainWindow_DirectoryChanged;

            // Создаем папку для кэша, если она не существует
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string searchAppFolder = Path.Combine(documentsPath, "SearchApp");
            if (!Directory.Exists(searchAppFolder))
            {
                Directory.CreateDirectory(searchAppFolder);
            }
            _cacheFilePath = Path.Combine(searchAppFolder, "cache.txt");

            LoadCache();
        }
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            SaveCache();
        }
        private void LoadCache()
        {
            if (File.Exists(_cacheFilePath))
            {
                string[] lines = File.ReadAllLines(_cacheFilePath);
                if (lines.Length == 2)
                {
                    StartDirectoryTextBox.Text = lines[0];
                    SearchPatternTextBox.Text = lines[1];
                }
            }
            else
            {
                File.WriteAllText(_cacheFilePath, "");
            }
        }
        private void SaveCache()
        {
            string startDirectory = StartDirectoryTextBox.Text;
            string searchPattern = SearchPatternTextBox.Text;
            File.WriteAllLines(_cacheFilePath, new[] { startDirectory, searchPattern });
        }
        private void Timer_Tick(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                ElapsedTimeLabel.Text = $"Время поиска: {_stopwatch.Elapsed.ToString(@"hh\:mm\:ss")}";
            });
        }

        private void MainWindow_FileFound(object sender, FileSystemItemEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                lock (_lock)
                {
                    _foundFiles.Add(new FileSystemItemViewModel { Name = e.FilePath });
                    UpdateTreeView(e.FilePath);
                    TotalFilesLabel.Content = $"Всего файлов: {_totalFilesChecked}";
                    if (IsMatchedFile(e.FilePath, SearchPatternTextBox.Text))
                    {
                        _matchedFilesCount++; // Увеличиваем количество файлов, соответствующих паттерну
                        MatchedFilesLabel.Content = $"Совпадающие файлы: {_matchedFilesCount}";
                    }
                }
            });
        }

        private bool IsMatchedFile(string filePath, string searchPattern)
        {
            string fileName = Path.GetFileName(filePath);
            return fileName != null && fileName.Contains(searchPattern);
        }

        private void MainWindow_DirectoryChanged(object sender, DirectoryChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                CurrentDirectoryLabel.Text = $"Текущая директория: {e.DirectoryPath}";
            });
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                return;
            }

            _foundFiles.Clear();
            FoundFilesTreeView.Items.Clear();
            _cancellationTokenSource = new CancellationTokenSource();
            var startDirectory = StartDirectoryTextBox.Text;
            var searchPattern = SearchPatternTextBox.Text;

            if (string.IsNullOrWhiteSpace(startDirectory) || string.IsNullOrWhiteSpace(searchPattern))
            {
                MessageBox.Show("Пожалуйста, укажите начальную директорию и паттерн поиска");
                return;
            }

            try
            {
                _stopwatch.Restart();
                _timer.Start();

                await SearchFilesAsync(startDirectory, searchPattern, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                // Операция отменена пользователем
            }
            finally
            {
                _stopwatch.Stop();
                _timer.Stop();
            }
        }

        private async Task SearchFilesAsync(string startDirectory, string searchPattern, CancellationToken cancellationToken)
        {
            await Task.Run(() => SearchDirectory(startDirectory, searchPattern, cancellationToken, startDirectory));
        }
        // Метод рекурсивного поиска файлов в директории
        private void SearchDirectory(string directory, string searchPattern, CancellationToken cancellationToken, string startDirectory)
        {
            DirectoryChanged?.Invoke(this, new DirectoryChangedEventArgs(directory));

            try
            {
                _totalFilesChecked++;

                var files = Directory.GetFiles(directory, searchPattern);

                foreach (var file in files)
                {
                    FileFound?.Invoke(this, new FileSystemItemEventArgs(Path.Combine(directory, file)));
                }

                // Получаем список поддиректорий и выполняем для каждой рекурсивный поиск
                var directories = Directory.GetDirectories(directory);
                foreach (var subDirectory in directories)
                {
                    SearchDirectory(subDirectory, searchPattern, cancellationToken, startDirectory);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Игнорируем директории, к которым нет доступа
            }
            catch (DirectoryNotFoundException)
            {
                // Игнорируем несуществующие директории
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    TotalFilesLabel.Content = $"Total Files: {_totalFilesChecked}";
                });
            }
        }

        /*
            UpdateTreeView
            Путь к файлу разбивается на части по символу разделителя директорий (Path.DirectorySeparatorChar), чтобы получить список каталогов в пути

            Для каждого каталога в разделенном пути проверяется, существует ли соответствующий элемент в древовидном представлении

            Если текущего элемента не существует:
            Создается новый элемент TreeViewItem
            Устанавливается заголовок элемента равным имени текущего каталога
            Этот новый элемент добавляется к родительскому элементу (в данном случае, к корневому элементу FoundFilesTreeView)
            Если текущий элемент уже существует:
            Поиск дочерних элементов текущего элемента, соответствующих текущему каталогу
            Если такой дочерний элемент не найден, создается новый элемент, аналогично описанному выше
            Если дочерний элемент уже существует, переходим к следующему каталогу
        */
        private void UpdateTreeView(string filePath)
        {
            var directories = filePath.Split(Path.DirectorySeparatorChar);
            TreeViewItem currentItem = null;

            foreach (var directory in directories)
            {
                if (string.IsNullOrEmpty(directory))
                    continue;

                if (currentItem == null)
                {
                    currentItem = FindChildItem(FoundFilesTreeView, directory);
                    if (currentItem == null)
                    {
                        currentItem = new TreeViewItem();
                        currentItem.Header = directory;
                        Dispatcher.Invoke(() =>
                        {
                            FoundFilesTreeView.Items.Add(currentItem);
                        });
                    }
                }
                else
                {
                    var existingItem = FindChildItem(currentItem, directory);
                    if (existingItem == null)
                    {
                        var newItem = new TreeViewItem();
                        newItem.Header = directory;
                        Dispatcher.Invoke(() =>
                        {
                            currentItem.Items.Add(newItem);
                        });
                        currentItem = newItem;
                    }
                    else
                    {
                        currentItem = existingItem;
                    }
                }
            }
        }
        /*
            FindChildItem
            Для каждого элемента в коллекции дочерних элементов parentItem.Items выполняется следующее:
            Проверяется, является ли текущий элемент экземпляром TreeViewItem
            Если текущий элемент является TreeViewItem и его заголовок равен указанному заголовку, возвращается этот элемент
            Если ни один из дочерних элементов не соответствует указанному заголовку, возвращается null
        */
        private TreeViewItem FindChildItem(ItemsControl parentItem, string header)
        {
            foreach (var item in parentItem.Items)
            {
                if (item is TreeViewItem treeViewItem && treeViewItem.Header.ToString() == header)
                {
                    return treeViewItem;
                }
            }
            return null;
        }
    }
    // Модель представления элемента файловой системы
    public class FileSystemItemViewModel
    {
        public string Name { get; set; }
    }
    // Аргументы событий для изменения директорий
    public class FileSystemItemEventArgs : EventArgs
    {
        public string FilePath { get; }

        public FileSystemItemEventArgs(string filePath)
        {
            FilePath = filePath;
        }
    }

    public class DirectoryChangedEventArgs : EventArgs
    {
        public string DirectoryPath { get; }

        public DirectoryChangedEventArgs(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }
    }
}