using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using FlowStudio.Services;
using Microsoft.Win32;

namespace FlowStudio;

public partial class MainWindow
{
    ContextMenu libraryActionsMenu = null!;
    Asset? libraryMenuAsset;
    CancellationTokenSource? libraryActionCancellation;
    Task? libraryActionTask;
    CancellationTokenSource? editorPreviewCancellation;
    readonly List<Task> editorPreviewTasks = [];
    MediaElement editorVideo = null!;
    TextBlock editorPreviewLabel = null!;
    Slider editorSeek = null!;
    Button editorPlay = null!;
    readonly DispatcherTimer editorPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    Asset? editorPreviewClip;
    bool editorPlaybackReady, editorPlaybackActive, editorPlayWhenReady, editorSeeking, editorSeekUpdating;
    LibraryViewPreferences libraryViewPreferences = new();
    ComboBox libraryTypeFilter = null!, librarySort = null!, libraryLayout = null!, libraryCardSize = null!;
    CheckBox libraryShowCardInfo = null!, libraryMutePreview = null!, libraryHoverAudio = null!, libraryClearPrompt = null!, libraryFavoritesOnly = null!;
    bool libraryViewInitializing;
    int libraryViewEditRevision;
    Task? libraryPreferencesTask;
    readonly SemaphoreSlim libraryPreferencesGate = new(1, 1);
    Asset? hoveredLibraryAsset;
    MediaElement? hoveredLibraryVideo;
    Grid? hoveredLibraryGrid;
    string libraryCollectionFilterProject = "", libraryCollectionFilterId = "", libraryCollectionFilterTitle = "";
    TextBlock libraryCollectionFilterLabel = null!;
    StackPanel libraryCollectionFilterPanel = null!;
    bool LibraryClearPromptAfterSuccess => libraryViewPreferences.ClearPromptAfterSuccess;
    bool LibraryPreviewMuted => libraryViewPreferences.PreviewMuted;
    bool LibraryHoverAudioEnabled => libraryViewPreferences.HoverAudioEnabled;

    /// <summary>Call once after InitializeExtraUi has built the library and editor.</summary>
    void InitializeLibraryActions()
    {
        libraryActionsMenu = new ContextMenu { Background = (Brush)FindResource("Panel"), Foreground = new SolidColorBrush(Color.FromRgb(237, 240, 235)), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(5) };
        libraryActionsMenu.Resources[typeof(MenuItem)] = CreateLibraryMenuItemStyle();
        LibraryList.ContextMenu = libraryActionsMenu;
        LibraryList.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(LibraryList, source) is ListBoxItem item)
            { item.IsSelected = true; item.Focus(); }
        };
        LibraryList.ContextMenuOpening += (_, e) =>
        {
            libraryMenuAsset = LibraryList.SelectedItem as Asset;
            libraryActionsMenu.Items.Clear();
            if (libraryMenuAsset is not { } asset) { e.Handled = true; return; }
            var ready = File.Exists(asset.Path) || !cloudMedia.TryGetValue(asset.Id, out var media) || media.Status == "success";
            var available = libraryActionTask?.IsCompleted != false && !closePending;
            AddLibraryMenuItem("Открыть / предпросмотр", OpenLibraryPreviewAsync, available && ready);
            AddLibraryMenuItem("Сохранить оригинал в папку…", SaveLibraryOriginalAsync, available && ready);
            AddLibraryMenuItem("Добавить в монтаж", AddLibrarySceneAsync, available && ready && !exporting && !generating && !preparingGeneration);
            libraryActionsMenu.Items.Add(new Separator());
            AddLibraryMenuItem("Использовать описание в студии", ReuseLibraryPromptAsync, available && !string.IsNullOrWhiteSpace(asset.Prompt));
            AddLibraryMenuItem("Показать файл в папке", RevealLibraryOriginalAsync, available && File.Exists(asset.Path));
            AddLibraryMenuItem("Сведения о материале", ShowLibraryMetadataAsync, available);
            AddCloudLibraryMenuItems(asset, available);
        };
        InitializeEditorVideoPreview();
        InitializeLibraryViewControls();
        LibraryList.PreviewMouseMove += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(LibraryList, source) is ListBoxItem item) UpdateLibraryHover(item);
            else StopLibraryHover();
        };
        LibraryList.MouseLeave += (_, _) => StopLibraryHover();
        LibraryPage.IsVisibleChanged += (_, _) => { if (!LibraryPage.IsVisible) StopLibraryHover(); };
    }

    void AddLibraryMenuItem(string caption, Func<Asset, CancellationToken, Task> action, bool enabled)
    {
        var item = new MenuItem { Header = caption, IsEnabled = enabled };
        item.Click += async (_, _) =>
        {
            if (closePending || libraryActionTask?.IsCompleted == false || libraryMenuAsset is not { } asset) return;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            libraryActionTask = completion.Task;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            libraryActionCancellation = cancellation;
            try { await action(asset, cancellation.Token); }
            catch (OperationCanceledException) { if (!closePending) Status("Действие с материалом остановлено"); }
            catch (Exception error) { if (!closePending) Error(error); }
            finally { cancellation.Dispose(); libraryActionCancellation = null; completion.TrySetResult(); }
        };
        libraryActionsMenu.Items.Add(item);
    }

    static Style CreateLibraryMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(237, 240, 235))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13, 10, 13, 10)));
        var template = new ControlTemplate(typeof(MenuItem));
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Surface"; border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(ContentPresenter.ContentSourceProperty, "Header"); content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true); border.AppendChild(content); template.VisualTree = border;
        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true }; hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(65, 79, 54)), "Surface")); template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .4, "Surface")); template.Triggers.Add(disabled);
        style.Setters.Add(new Setter(Control.TemplateProperty, template)); return style;
    }

    async Task OpenLibraryPreviewAsync(Asset asset, CancellationToken ct)
    {
        await EnsureCloudAssetAsync(asset, ct);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(asset.Path)) throw new FileNotFoundException("Оригинал материала не найден.");
        SelectAsset(asset); SwitchPage("studio");
    }

    async Task SaveLibraryOriginalAsync(Asset asset, CancellationToken ct)
    {
        var folder = new OpenFolderDialog { Title = "Куда сохранить оригинал Flow" };
        if (folder.ShowDialog(this) != true) return;
        await EnsureCloudAssetAsync(asset, ct);
        var result = await LibraryFileActions.CopyOriginalAsync(asset.Path, folder.FolderName, asset.Name, ct);
        Status($"Оригинал сохранён: {result.Path}");
    }

    async Task AddLibrarySceneAsync(Asset asset, CancellationToken ct)
    {
        if (exporting || generating || preparingGeneration || !await projectOperation.WaitAsync(0, ct))
            throw new InvalidOperationException("Дождитесь завершения текущей задачи.");
        var targetProject = projectPath; var revision = accountRevision;
        try
        {
            await EnsureCloudAssetAsync(asset, ct); ct.ThrowIfCancellationRequested();
            if (revision != accountRevision || targetProject != projectPath || closePending) throw new OperationCanceledException();
            if (!File.Exists(asset.Path)) throw new FileNotFoundException("Оригинал материала не найден.");
            if (!assets.Any(value => value.Id == asset.Id)) assets.Add(asset);
            var shot = CreateShot(asset); shots.Add(shot); editorList.SelectedItem = shot;
            UpdateCounts(); MarkDirty(); await SaveCurrentAsync(); SwitchPage("editor"); Status("Материал добавлен в монтаж");
        }
        finally { projectOperation.Release(); }
    }

    Task ReuseLibraryPromptAsync(Asset asset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PromptBox.Text = asset.Prompt;
        ModeBox.SelectedIndex = asset.Kind == "image" ? 1 : 0;
        if (flowModels.Any(model => model.Kind == asset.Kind && model.ModelKey == asset.Model)) ModelKeyBox.Text = asset.Model;
        RefreshComposerModels(); MarkDirty(); SwitchPage("studio"); PromptBox.Focus(); Status("Описание материала перенесено в студию");
        return Task.CompletedTask;
    }

    Task RevealLibraryOriginalAsync(Asset asset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(asset.Path)) throw new FileNotFoundException("Сначала сохраните оригинал материала.");
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(Path.GetFullPath(asset.Path)); Process.Start(start);
        return Task.CompletedTask;
    }

    async Task ShowLibraryMetadataAsync(Asset asset, CancellationToken ct)
    {
        var details = new StringBuilder();
        details.AppendLine(asset.Name).AppendLine(asset.Kind == "video" ? "Видео" : "Изображение").AppendLine($"Создано: {asset.Created:dd.MM.yyyy HH:mm}");
        if (!string.IsNullOrWhiteSpace(asset.CloudProjectTitle)) details.AppendLine("Проект Flow: " + asset.CloudProjectTitle);
        if (!string.IsNullOrWhiteSpace(asset.Model)) details.AppendLine("Модель: " + asset.Model);
        if (File.Exists(asset.Path))
        {
            details.AppendLine($"Оригинал: {new FileInfo(asset.Path).Length / 1024d / 1024d:0.##} МБ");
            if (asset.Kind == "video")
            {
                try { var info = await exporter.InspectAsync(asset.Path, ct); details.AppendLine($"{info.Width} × {info.Height} · {info.DurationSeconds:0.##} сек · " + (info.HasAudio ? "со звуком" : "без звука")); }
                catch (Exception error) when (error is not OperationCanceledException) { details.AppendLine("Параметры видео не прочитаны: " + error.Message); }
            }
            details.AppendLine("Файл: " + asset.Path);
        }
        else details.AppendLine("Оригинал находится в Flow; на устройство ещё не скачан.");
        if (!string.IsNullOrWhiteSpace(asset.Prompt)) details.AppendLine().AppendLine("Описание:").AppendLine(asset.Prompt.Length > 2000 ? asset.Prompt[..2000] + "…" : asset.Prompt);
        ct.ThrowIfCancellationRequested(); if (!closePending) MessageBox.Show(this, details.ToString(), "Сведения о материале", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void InitializeEditorVideoPreview()
    {
        if (!extraPages.TryGetValue("editor", out var pageBorder) || pageBorder.Child is not ScrollViewer { Content: StackPanel body }) return;
        var preview = new Grid { Height = 265, Margin = new Thickness(0, 0, 0, 14), Background = new SolidColorBrush(Color.FromRgb(8, 13, 15)) };
        preview.RowDefinitions.Add(new RowDefinition()); preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorVideo = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
        preview.Children.Add(editorVideo);
        var controls = new DockPanel { Margin = new Thickness(10, 7, 10, 9) }; Grid.SetRow(controls, 1); preview.Children.Add(controls);
        editorPlay = new Button { Content = "▶ Обрезанный клип", Margin = new Thickness(0, 0, 10, 0) }; DockPanel.SetDock(editorPlay, Dock.Left); controls.Children.Add(editorPlay);
        editorPreviewLabel = new TextBlock { Text = "Выберите видеоклип в монтаже", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(editorPreviewLabel, Dock.Right); controls.Children.Add(editorPreviewLabel);
        editorSeek = new Slider { Minimum = 0, Maximum = 1, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center, MinWidth = 80 }; controls.Children.Add(editorSeek);
        body.Children.Insert(Math.Min(2, body.Children.Count), preview);
        editorPlay.Click += (_, _) =>
        {
            if (editorPlaybackActive) PauseEditorVideo();
            else if (editorPlaybackReady && ReferenceEquals(editorList.SelectedItem, editorPreviewClip)) PlayEditorRange();
            else StartEditorPreviewPreparation(play: true);
        };
        editorList.SelectionChanged += (_, _) => StartEditorPreviewPreparation(play: false);
        editorSeek.PreviewMouseLeftButtonDown += (_, _) => editorSeeking = true;
        editorSeek.PreviewMouseLeftButtonUp += (_, _) => { editorSeeking = false; if (editorPlaybackReady) editorVideo.Position = TimeSpan.FromSeconds(editorSeek.Value); };
        editorSeek.ValueChanged += (_, _) => { if (!editorSeekUpdating && editorPlaybackReady) editorVideo.Position = TimeSpan.FromSeconds(editorSeek.Value); };
        editorVideo.MediaOpened += (_, _) =>
        {
            if (closePending) return;
            editorPlaybackReady = editorVideo.NaturalDuration.HasTimeSpan && editorPreviewClip is not null;
            if (!editorPlaybackReady) return;
            var (start, end) = EditorPreviewBounds(); editorSeek.Minimum = start; editorSeek.Maximum = Math.Max(start + .001, end); editorSeek.Value = start; editorSeek.IsEnabled = true;
            editorVideo.Position = TimeSpan.FromSeconds(start);
            if (editorPlayWhenReady) PlayEditorRange(); else PauseEditorVideo();
        };
        editorVideo.MediaEnded += (_, _) => PauseEditorVideo();
        editorVideo.MediaFailed += (_, args) => { PauseEditorVideo(); editorPlaybackReady = false; editorPreviewLabel.Text = "Windows не смог открыть этот видеокодек"; };
        editorVideo.Unloaded += (_, _) => PauseEditorVideo();
        pageBorder.IsVisibleChanged += (_, _) => { if (!pageBorder.IsVisible) PauseEditorVideo(); };
        editorPreviewTimer.Tick += (_, _) =>
        {
            if (!editorPlaybackReady || editorPreviewClip is null) return;
            var (start, end) = EditorPreviewBounds();
            if (editorPlaybackActive && editorVideo.Position.TotalSeconds >= end) { PauseEditorVideo(); editorVideo.Position = TimeSpan.FromSeconds(start); }
            if (!editorSeeking)
            {
                editorSeekUpdating = true;
                try { editorSeek.Value = Math.Clamp(editorVideo.Position.TotalSeconds, editorSeek.Minimum, editorSeek.Maximum); }
                finally { editorSeekUpdating = false; }
            }
            editorPreviewLabel.Text = $"{editorVideo.Position.TotalSeconds:0.0} / {end:0.0} сек" + (editorPreviewClip.Volume > 1 && !editorPreviewClip.Muted ? " · усиление >100% при экспорте" : "");
        };
    }

    void StartEditorPreviewPreparation(bool play)
    {
        if (editorVideo is null || closePending) return;
        editorPreviewCancellation?.Cancel(); PauseEditorVideo();
        if (editorList.SelectedItem is not Asset { Kind: "video" } clip)
        { editorVideo.Source = null; editorPlaybackReady = false; editorSeek.IsEnabled = false; editorPreviewLabel.Text = "Выберите видеоклип в монтаже"; return; }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); editorPreviewCancellation = cancellation;
        editorPreviewTasks.RemoveAll(task => task.IsCompleted);
        editorPreviewTasks.Add(PrepareEditorVideoAsync(clip, play, cancellation));
    }

    async Task PrepareEditorVideoAsync(Asset clip, bool play, CancellationTokenSource cancellation)
    {
        try
        {
            editorPreviewLabel.Text = "Открываем оригинал…";
            await EnsureCloudAssetAsync(clip, cancellation.Token); cancellation.Token.ThrowIfCancellationRequested();
            if (editorList.SelectedItem != clip || closePending) return;
            if (!File.Exists(clip.Path)) throw new FileNotFoundException("Оригинал клипа не найден.");
            editorPreviewClip = clip; editorPlayWhenReady = play; editorPlaybackReady = false; editorSeek.IsEnabled = false;
            editorVideo.Stop(); editorVideo.Source = new Uri(Path.GetFullPath(clip.Path)); editorVideo.Play(); editorVideo.Pause();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { editorPreviewLabel.Text = error.Message; }
        finally { if (ReferenceEquals(editorPreviewCancellation, cancellation)) editorPreviewCancellation = null; cancellation.Dispose(); }
    }

    (double Start, double End) EditorPreviewBounds()
    {
        var duration = editorVideo.NaturalDuration.HasTimeSpan ? editorVideo.NaturalDuration.TimeSpan.TotalSeconds : 0;
        var start = Math.Clamp(editorPreviewClip?.TrimStartSeconds ?? 0, 0, Math.Max(0, duration - .001));
        var end = Math.Clamp(editorPreviewClip?.TrimEndSeconds ?? duration, start, duration);
        return (start, end);
    }

    void PlayEditorRange()
    {
        if (!editorPlaybackReady || editorPreviewClip is null) return;
        var (start, end) = EditorPreviewBounds();
        editorSeek.Minimum = start; editorSeek.Maximum = Math.Max(start + .001, end);
        if (editorVideo.Position.TotalSeconds < start || editorVideo.Position.TotalSeconds >= end) editorVideo.Position = TimeSpan.FromSeconds(start);
        editorVideo.Volume = editorPreviewClip.Muted || LibraryPreviewMuted ? 0 : Math.Clamp(editorPreviewClip.Volume, 0, 1);
        editorVideo.Play(); editorPlaybackActive = true; editorPlay.Content = "Ⅱ Пауза"; editorPreviewTimer.Start();
    }

    void PauseEditorVideo()
    {
        editorPreviewTimer.Stop();
        if (editorVideo is not null) editorVideo.Pause();
        editorPlaybackActive = false; if (editorPlay is not null) editorPlay.Content = "▶ Обрезанный клип";
    }

    /// <summary>Call before acquiring projectOperation during close, and before replacing the account session.</summary>
    async Task CancelLibraryActionsAsync()
    {
        libraryActionCancellation?.Cancel(); editorPreviewCancellation?.Cancel(); PauseEditorVideo(); StopLibraryHover();
        if (libraryActionsMenu is not null) libraryActionsMenu.IsOpen = false;
        libraryMenuAsset = null; editorPlayWhenReady = false;
        if (libraryActionTask is { } action) await action;
        if (editorPreviewTasks.Count > 0) await Task.WhenAll(editorPreviewTasks.ToArray());
        if (libraryPreferencesTask is { } saving) await saving;
        await CancelManagementUiAsync();
        if (editorVideo is not null) { editorVideo.Close(); editorVideo.Source = null; }
        editorPreviewClip = null;
        editorPlaybackReady = false;
        libraryCollectionFilterProject = libraryCollectionFilterId = libraryCollectionFilterTitle = "";
    }

    void InitializeLibraryViewControls()
    {
        var grid = (Grid)LibraryPage.Child;
        grid.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement element in grid.Children) if (Grid.GetRow(element) >= 1) Grid.SetRow(element, Grid.GetRow(element) + 1);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; Grid.SetRow(panel, 1); grid.Children.Add(panel);
        var row = new WrapPanel(); panel.Children.Add(row);
        libraryTypeFilter = AddLibraryChoice(row, new[] { "Все материалы", "Только видео", "Только изображения" }, 155);
        librarySort = AddLibraryChoice(row, new[] { "Сначала новые", "Сначала старые", "По названию" }, 150);
        libraryLayout = AddLibraryChoice(row, new[] { "Сетка", "Пакеты генерации" }, 165);
        libraryCardSize = AddLibraryChoice(row, new[] { "Карточки S", "Карточки M", "Карточки L" }, 130);
        libraryCollectionFilterPanel = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 8) };
        libraryCollectionFilterLabel = new TextBlock { Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(0, 0, 12, 0) }; libraryCollectionFilterPanel.Children.Add(libraryCollectionFilterLabel);
        var clearCollection = new Button { Content = "Показать всё", Padding = new Thickness(9, 5, 9, 5) };
        clearCollection.Click += (_, _) => { libraryCollectionFilterProject = libraryCollectionFilterId = libraryCollectionFilterTitle = ""; ApplyLibraryView(); }; libraryCollectionFilterPanel.Children.Add(clearCollection); panel.Children.Add(libraryCollectionFilterPanel);
        var options = new WrapPanel();
        libraryShowCardInfo = AddLibraryOption(options, "Сведения на карточках");
        libraryFavoritesOnly = AddLibraryOption(options, "Только избранное Flow");
        libraryMutePreview = AddLibraryOption(options, "Без звука в предпросмотре");
        libraryHoverAudio = AddLibraryOption(options, "Звук при наведении на скачанные видео");
        libraryClearPrompt = AddLibraryOption(options, "Очищать описание после успешного создания");
        panel.Children.Add(new Expander { Header = "Настройки вида и предпросмотра · на этом компьютере", Content = options, Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
        libraryViewInitializing = true;
        SetLibraryViewControls(); libraryViewInitializing = false; ApplyLibraryView();
        libraryPreferencesTask = LoadLibraryViewPreferencesAsync();
    }

    ComboBox AddLibraryChoice(Panel host, string[] choices, double width)
    {
        var box = new ComboBox { ItemsSource = choices, Width = width, Margin = new Thickness(0, 0, 8, 6) }; host.Children.Add(box);
        box.SelectionChanged += (_, _) => ChangeLibraryViewPreferences(); return box;
    }
    CheckBox AddLibraryOption(Panel host, string text)
    {
        var box = new CheckBox { Content = text, Margin = new Thickness(0, 9, 18, 0) }; host.Children.Add(box);
        box.Click += (_, _) => ChangeLibraryViewPreferences(); return box;
    }
    async Task LoadLibraryViewPreferencesAsync()
    {
        var revision = libraryViewEditRevision; LibraryViewPreferences loaded;
        try { loaded = await LibraryViewPreferences.LoadAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { loaded = new(); }
        if (closePending || revision != libraryViewEditRevision) return;
        libraryViewPreferences = loaded;
        libraryViewInitializing = true; SetLibraryViewControls(); libraryViewInitializing = false; ApplyLibraryView();
    }
    void SetLibraryViewControls()
    {
        libraryTypeFilter.SelectedIndex = libraryViewPreferences.TypeFilter switch { "video" => 1, "image" => 2, _ => 0 };
        librarySort.SelectedIndex = libraryViewPreferences.Sort switch { "oldest" => 1, "name" => 2, _ => 0 };
        libraryLayout.SelectedIndex = libraryViewPreferences.Layout == "batch" ? 1 : 0;
        libraryCardSize.SelectedIndex = libraryViewPreferences.CardSize switch { "small" => 0, "large" => 2, _ => 1 };
        libraryShowCardInfo.IsChecked = libraryViewPreferences.ShowCardInfo; libraryMutePreview.IsChecked = libraryViewPreferences.PreviewMuted;
        libraryFavoritesOnly.IsChecked = libraryViewPreferences.FavoritesOnly;
        libraryHoverAudio.IsChecked = libraryViewPreferences.HoverAudioEnabled; libraryClearPrompt.IsChecked = libraryViewPreferences.ClearPromptAfterSuccess;
    }
    void ChangeLibraryViewPreferences()
    {
        if (libraryViewInitializing || libraryClearPrompt is null) return;
        libraryViewEditRevision++;
        libraryViewPreferences = new()
        {
            TypeFilter = libraryTypeFilter.SelectedIndex switch { 1 => "video", 2 => "image", _ => "all" },
            Sort = librarySort.SelectedIndex switch { 1 => "oldest", 2 => "name", _ => "newest" },
            Layout = libraryLayout.SelectedIndex == 1 ? "batch" : "grid",
            CardSize = libraryCardSize.SelectedIndex switch { 0 => "small", 2 => "large", _ => "medium" },
            ShowCardInfo = libraryShowCardInfo.IsChecked == true, FavoritesOnly = libraryFavoritesOnly.IsChecked == true, PreviewMuted = libraryMutePreview.IsChecked == true,
            HoverAudioEnabled = libraryHoverAudio.IsChecked == true, ClearPromptAfterSuccess = libraryClearPrompt.IsChecked == true
        };
        ApplyLibraryView();
        var snapshot = libraryViewPreferences;
        libraryPreferencesTask = SaveLibraryViewPreferencesAsync(snapshot);
    }
    async Task SaveLibraryViewPreferencesAsync(LibraryViewPreferences snapshot)
    {
        await libraryPreferencesGate.WaitAsync();
        try { await snapshot.SaveAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { if (!closePending) Status("Настройки вида не сохранены: " + error.Message); }
        finally { libraryPreferencesGate.Release(); }
    }

    /// <summary>Call at the end of root LibrarySearch_TextChanged / scope refresh.</summary>
    void ApplyLibraryView()
    {
        if (LibraryList?.ItemsSource is null) return;
        StopLibraryHover();
        if (libraryScope?.SelectedIndex == 1) libraryCollectionFilterProject = libraryCollectionFilterId = libraryCollectionFilterTitle = "";
        if (libraryCollectionFilterPanel is not null) libraryCollectionFilterPanel.Visibility = string.IsNullOrEmpty(libraryCollectionFilterId) ? Visibility.Collapsed : Visibility.Visible;
        if (libraryCollectionFilterLabel is not null) libraryCollectionFilterLabel.Text = "Коллекция: " + libraryCollectionFilterTitle;
        Resources["LibraryCardWidth"] = libraryViewPreferences.CardSize switch { "small" => 165d, "large" => 285d, _ => 218d };
        Resources["LibraryThumbHeight"] = libraryViewPreferences.CardSize switch { "small" => 85d, "large" => 155d, _ => 115d };
        Resources["LibraryCardInfoVisibility"] = libraryViewPreferences.ShowCardInfo ? Visibility.Visible : Visibility.Collapsed;
        VideoPreview.Volume = LibraryPreviewMuted ? 0 : 1;
        if (editorPreviewClip is { } clip && editorVideo is not null) editorVideo.Volume = clip.Muted || LibraryPreviewMuted ? 0 : Math.Clamp(clip.Volume, 0, 1);
        var view = CollectionViewSource.GetDefaultView(LibraryList.ItemsSource);
        if (view is null) return;
        using (view.DeferRefresh())
        {
            var query = LibrarySearch?.Text ?? "";
            view.Filter = item => item is Asset asset && (libraryViewPreferences.TypeFilter == "all" || libraryViewPreferences.TypeFilter == asset.Kind)
                && (!libraryViewPreferences.FavoritesOnly || asset.IsFavorite)
                && (string.IsNullOrEmpty(libraryCollectionFilterId) || asset.CloudProjectId == libraryCollectionFilterProject && asset.CloudCollectionId == libraryCollectionFilterId)
                && (asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || asset.Prompt.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (view.CanSort)
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(libraryViewPreferences.Sort == "name" ? new SortDescription(nameof(Asset.Name), ListSortDirection.Ascending)
                    : new SortDescription(nameof(Asset.Created), libraryViewPreferences.Sort == "oldest" ? ListSortDirection.Ascending : ListSortDirection.Descending));
            }
            if (view.CanGroup)
            {
                view.GroupDescriptions.Clear();
                if (libraryViewPreferences.Layout == "batch")
                {
                    view.GroupDescriptions.Add(new LibraryBatchGroupDescription());
                }
            }
        }
        LibraryList.GroupStyle.Clear();
        if(view.CanGroup&&libraryViewPreferences.Layout=="batch")LibraryList.GroupStyle.Add(new GroupStyle{HeaderTemplate=CreateLibraryBatchHeaderTemplate()});
        UpdateLibraryFilteredState();
    }

    /// <summary>Call at the end of root UpdateCounts after its general counters.</summary>
    void UpdateLibraryFilteredState()
    {
        if (page != "library" || LibraryList?.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(LibraryList.ItemsSource);
        if (view is null) return;
        var count = view.Cast<object>().OfType<Asset>().Count();
        AssetCount.Text = $"{count} {Plural(count, "материал", "материала", "материалов")}".ToUpperInvariant();
        EmptyLibrary.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var sourceCount = LibraryList.ItemsSource.Cast<object>().OfType<Asset>().Count();
        if (count == 0 && sourceCount > 0) EmptyLibrary.Text = "По выбранным фильтрам ничего не найдено.\nИзмените поиск, тип материала или коллекцию.";
    }

    static DataTemplate CreateLibraryBatchHeaderTemplate()
    {
        var template = new DataTemplate();
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); row.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 16, 4, 8));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding("Items[0].Name")); title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); title.SetValue(FrameworkElement.MaxWidthProperty, 440d); row.AppendChild(title);
        var count = new FrameworkElementFactory(typeof(TextBlock)); count.SetBinding(TextBlock.TextProperty, new Binding("ItemCount") { StringFormat = "  ·  {0} материалов" }); row.AppendChild(count);
        template.VisualTree = row; return template;
    }
    sealed class LibraryBatchGroupDescription : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, System.Globalization.CultureInfo culture) => item is Asset asset
            ? string.IsNullOrWhiteSpace(asset.CloudWorkflowId) ? "media:" + asset.Id : "workflow:" + asset.CloudWorkflowId : item;
    }

    void UpdateLibraryHover(ListBoxItem item)
    {
        if (item.DataContext is not Asset asset || ReferenceEquals(asset, hoveredLibraryAsset)) return;
        StopLibraryHover(); hoveredLibraryAsset = asset;
        if (asset.Kind != "video" || !File.Exists(asset.Path)) return;
        var grid = FindLibraryThumbnailGrid(item); if (grid is null) return;
        var video = new MediaElement { Source = new Uri(Path.GetFullPath(asset.Path)), IsHitTestVisible = false, LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual, Stretch = Stretch.UniformToFill, Volume = LibraryPreviewMuted || !LibraryHoverAudioEnabled ? 0 : 1 };
        hoveredLibraryVideo = video; hoveredLibraryGrid = grid; grid.Children.Add(video);
        video.MediaEnded += (_, _) => { if (ReferenceEquals(hoveredLibraryVideo, video)) { video.Position = TimeSpan.Zero; video.Play(); } };
        video.MediaFailed += (_, _) => { if (ReferenceEquals(hoveredLibraryVideo, video)) StopLibraryHover(); };
        video.Play();
    }
    static Grid? FindLibraryThumbnailGrid(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Grid grid && grid.Children.OfType<Image>().Any()) return grid;
            if (FindLibraryThumbnailGrid(child) is { } match) return match;
        }
        return null;
    }
    void StopLibraryHover()
    {
        if (hoveredLibraryVideo is { } video) { video.Stop(); video.Close(); hoveredLibraryGrid?.Children.Remove(video); }
        hoveredLibraryVideo = null; hoveredLibraryGrid = null; hoveredLibraryAsset = null;
    }
}
