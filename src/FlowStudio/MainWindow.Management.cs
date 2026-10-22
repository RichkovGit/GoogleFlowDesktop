using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlowStudio.Services;

namespace FlowStudio;

public partial class MainWindow
{
    readonly ObservableCollection<FlowCollectionInfo> managementCollections = [];
    readonly ObservableCollection<FlowCollectionInfo> managementArchivedCollections = [];
    readonly ObservableCollection<FlowCloudMediaInfo> managementTrashedMedia = [];
    ComboBox managementProject = null!;
    ListBox managementCollectionList = null!, managementArchivedList = null!, managementTrashList = null!;
    TextBlock managementStatus = null!;
    CancellationTokenSource? managementCancellation;
    Task? managementTask;
    bool managementInitializing;
    sealed record ManagementSnapshot(string ProjectId, string Credential, int Revision);
    sealed record CollectionChoice(string? Id, string Label);

    /// <summary>Call after InitializeLibraryActions. All mutations require the user's click.</summary>
    void InitializeManagementUi()
    {
        var nav = (StackPanel)StudioNav.Parent;
        var heading = nav.Children.OfType<TextBlock>().First(value => value.Text == "ПРОЕКТЫ");
        AddNavigation(nav, nav.Children.IndexOf(heading) + 2, "management", "▣     Коллекции и корзина");
        var pageBorder = CreateFeaturePage("management", "Коллекции и корзина Flow", "Коллекции, избранное и восстановление материалов выбранного проекта аккаунта.", out var body);
        managementProject = new ComboBox { ItemsSource = cloudProjects, DisplayMemberPath = "Title", MinWidth = 300, MaxWidth = 650, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) }; body.Children.Add(managementProject);
        var refresh = ActionButton("↻ Обновить коллекции и корзину", () => RunManagementOperationAsync(null, async (service, snapshot, ct) => await ReadManagementStateAsync(service, snapshot, ct), guardProject: false)); body.Children.Add(refresh);
        body.Children.Add(new TextBlock { Text = "Коллекции", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 9) });
        managementCollectionList = new ListBox { ItemsSource = managementCollections, DisplayMemberPath = "Title", Height = 200 }; body.Children.Add(managementCollectionList);
        var collectionActions = new WrapPanel();
        collectionActions.Children.Add(ActionButton("Создать коллекцию", () => RunManagementOperationAsync(null, CreateManagementCollectionAsync), true));
        collectionActions.Children.Add(ActionButton("Открыть", OpenManagementCollectionAsync));
        collectionActions.Children.Add(ActionButton("Переименовать", () => RunManagementOperationAsync(null, RenameManagementCollectionAsync)));
        collectionActions.Children.Add(ActionButton("Переместить", () => RunManagementOperationAsync(null, MoveManagementCollectionAsync))); body.Children.Add(collectionActions);
        managementCollectionList.MouseDoubleClick += async (_, _) => { try { await OpenManagementCollectionAsync(); } catch (Exception error) { if (!closePending) Error(error); } };
        managementCollectionList.PreviewMouseRightButtonDown += SelectManagementContextRow;
        managementCollectionList.ContextMenu = new ContextMenu();
        managementCollectionList.ContextMenuOpening += (_, args) =>
        {
            if (managementCollectionList.SelectedItem is not FlowCollectionInfo) { args.Handled = true; return; }
            var menu = managementCollectionList.ContextMenu; menu.Items.Clear();
            AddManagementMenuItem(menu, "Открыть коллекцию", OpenManagementCollectionAsync);
            AddManagementMenuItem(menu, "Переименовать", () => RunManagementOperationAsync(null, RenameManagementCollectionAsync));
            AddManagementMenuItem(menu, "Создать вложенную коллекцию…", () => RunManagementOperationAsync(null, CreateManagementSubcollectionAsync));
            AddManagementMenuItem(menu, "Переместить в другую коллекцию…", () => RunManagementOperationAsync(null, MoveManagementCollectionAsync));
            AddManagementMenuItem(menu, "Добавить в избранное", () => RunManagementOperationAsync(null, (service, snapshot, ct) => FavoriteManagementCollectionAsync(service, snapshot, true, ct)));
            AddManagementMenuItem(menu, "Убрать из избранного", () => RunManagementOperationAsync(null, (service, snapshot, ct) => FavoriteManagementCollectionAsync(service, snapshot, false, ct)));
            AddManagementMenuItem(menu, "Обложка из выбранного материала", () => RunManagementOperationAsync(null, SetManagementCollectionCoverAsync));
            AddManagementMenuItem(menu, "Сбросить обложку", () => RunManagementOperationAsync(null, ClearManagementCollectionCoverAsync));
            AddManagementMenuItem(menu, "Архивировать папку коллекции", () => RunManagementOperationAsync(null, ArchiveManagementCollectionAsync));
        };
        body.Children.Add(new TextBlock { Text = "Корзина · группы генерации", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 9) });
        managementTrashList = new ListBox { ItemsSource = managementTrashedMedia, DisplayMemberPath = "Title", Height = 180 }; body.Children.Add(managementTrashList);
        body.Children.Add(ActionButton("Восстановить выбранную группу генерации", () => RunManagementOperationAsync(null, RestoreManagementMediaAsync), true));
        managementTrashList.PreviewMouseRightButtonDown += SelectManagementContextRow;
        managementTrashList.ContextMenu = new ContextMenu();
        managementTrashList.ContextMenuOpening += (_, args) =>
        {
            if (managementTrashList.SelectedItem is not FlowCloudMediaInfo) { args.Handled = true; return; }
            var menu = managementTrashList.ContextMenu; menu.Items.Clear();
            AddManagementMenuItem(menu, "Восстановить группу генерации", () => RunManagementOperationAsync(null, RestoreManagementMediaAsync));
        };
        body.Children.Add(new TextBlock { Text = "Архивированные папки коллекций", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 9) });
        managementArchivedList = new ListBox { ItemsSource = managementArchivedCollections, DisplayMemberPath = "Title", Height = 130 }; body.Children.Add(managementArchivedList);
        body.Children.Add(ActionButton("Восстановить папку коллекции", () => RunManagementOperationAsync(null, RestoreManagementCollectionAsync)));
        body.Children.Add(new TextBlock { Text = "Архивирование папки меняет состояние самой коллекции. Рекурсивное удаление её содержимого здесь не выполняется.", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 12) });
        managementStatus = new TextBlock { Text = "Выберите проект аккаунта.", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 10, 0, 0) }; body.Children.Add(managementStatus);
        managementProject.SelectionChanged += async (_, _) => { if (!managementInitializing && pageBorder.IsVisible) await RunManagementOperationAsync(null, ReadManagementStateAsync, guardProject: false); };
        pageBorder.IsVisibleChanged += async (_, _) =>
        {
            if (!pageBorder.IsVisible) return;
            managementInitializing = true;
            if (managementProject.SelectedItem is not CloudProjectCard) managementProject.SelectedItem = cloudProjects.FirstOrDefault(project => project.Id == CloudProjectBox.Text.Trim().Replace("projects/", "")) ?? cloudProjects.FirstOrDefault();
            managementInitializing = false;
            await RunManagementOperationAsync(null, ReadManagementStateAsync, guardProject: false);
        };
        InitializeProjectManagementContextMenu();
    }

    void AddCloudLibraryMenuItems(Asset asset, bool available)
    {
        var info = CurrentCloudInfo(asset); if (info is null || string.IsNullOrWhiteSpace(info.WorkflowId)) return;
        var variants = cloudAssets.Count(value => value.CloudProjectId == info.ProjectId && value.CloudWorkflowId == info.WorkflowId);
        var group = variants > 1 ? $"группу ({variants})" : "материал";
        libraryActionsMenu.Items.Add(new Separator());
        AddLibraryMenuItem("Переименовать " + group + " в Flow…", async (item, ct) => await RunLibraryMutationAsync(item, async (service, snapshot, media, token) =>
        {
            var title = RequestManagementText("Название в Flow", "Название группы генерации", media.Title);
            if (title is null) throw new OperationCanceledException();
            CheckManagementSnapshot(snapshot, token); await service.RenameMediaAsync(snapshot.ProjectId, media.WorkflowId, title, snapshot.Credential, token);
            CheckManagementSnapshot(snapshot, token); UpdateLocalWorkflowMetadata(media, title: title);
        }, ct), available);
        AddLibraryMenuItem("Добавить в избранное Flow", (item, ct) => RunLibraryMutationAsync(item, async (service, snapshot, media, token) => { await service.SetFavoriteAsync(snapshot.ProjectId, media.WorkflowId, true, snapshot.Credential, token); CheckManagementSnapshot(snapshot, token); UpdateLocalWorkflowMetadata(media, favorite: true); }, ct), available);
        AddLibraryMenuItem("Убрать из избранного Flow", (item, ct) => RunLibraryMutationAsync(item, async (service, snapshot, media, token) => { await service.SetFavoriteAsync(snapshot.ProjectId, media.WorkflowId, false, snapshot.Credential, token); CheckManagementSnapshot(snapshot, token); UpdateLocalWorkflowMetadata(media, favorite: false); }, ct), available);
        AddLibraryMenuItem("Сделать обложкой проекта Flow", (item, ct) => RunLibraryMutationAsync(item, (service, snapshot, media, token) => service.SetProjectCoverAsync(snapshot.ProjectId, media.MediaName, snapshot.Credential, token), ct), available);
        AddLibraryMenuItem("Переместить " + group + " в коллекцию…", MoveLibraryMediaToCollectionAsync, available);
        AddLibraryMenuItem(info.IsTrashed ? "Восстановить " + group + " в Flow" : "Переместить " + group + " в корзину Flow", (item, ct) => RunLibraryMutationAsync(item, async (service, snapshot, media, token) => { await service.SetMediaTrashedAsync(snapshot.ProjectId, media.WorkflowId, !media.IsTrashed, snapshot.Credential, token); CheckManagementSnapshot(snapshot, token); UpdateLocalWorkflowMetadata(media, trashed: !media.IsTrashed); }, ct), available);
    }

    FlowCloudMediaInfo? CurrentCloudInfo(Asset asset) => cloudMedia.TryGetValue(asset.Id, out var direct) ? direct
        : cloudMedia.Values.FirstOrDefault(value => value.MediaName == asset.CloudMediaName && value.ProjectId == asset.CloudProjectId);

    void UpdateLocalWorkflowMetadata(FlowCloudMediaInfo media, string? title = null, bool? favorite = null, bool? trashed = null, string? collection = null)
    {
        var changed = false;
        foreach (var asset in assets.Concat(shots).Concat(cloudAssets).Where(value => value.CloudProjectId == media.ProjectId && value.CloudWorkflowId == media.WorkflowId))
        {
            if (title is not null) asset.Name = title;
            if (favorite is { } isFavorite) asset.IsFavorite = isFavorite;
            if (trashed is { } isTrashed) asset.IsTrashed = isTrashed;
            if (collection is not null) asset.CloudCollectionId = collection;
            changed = true;
        }
        if (changed) { MarkDirty(); LibraryList.Items.Refresh(); editorList.Items.Refresh(); }
    }

    async Task RunLibraryMutationAsync(Asset asset, Func<FlowManagementService, ManagementSnapshot, FlowCloudMediaInfo, CancellationToken, Task> action, CancellationToken ct)
    {
        if (closePending || generating || exporting || preparingGeneration || loginInProgress || managementTask?.IsCompleted == false || !await projectOperation.WaitAsync(0, ct))
            throw new InvalidOperationException("Дождитесь завершения текущей задачи.");
        try
        {
            var media = CurrentCloudInfo(asset) ?? throw new InvalidOperationException("Обновите медиатеку активного аккаунта.");
            var snapshot = CaptureManagementSnapshot(media.ProjectId); CheckManagementSnapshot(snapshot, ct);
            await action(new FlowManagementService(http), snapshot, media, ct); CheckManagementSnapshot(snapshot, ct);
            await RefreshCloudLibraryAsync(ct); Status("Изменение отправлено в Flow");
        }
        finally { projectOperation.Release(); }
    }

    async Task MoveLibraryMediaToCollectionAsync(Asset asset, CancellationToken ct) => await RunLibraryMutationAsync(asset, async (service, snapshot, media, token) =>
    {
        var collections = await service.ListCollectionsAsync(snapshot.ProjectId, snapshot.Credential, token); CheckManagementSnapshot(snapshot, token);
        var choice = PickManagementCollection(collections.Where(value => !value.IsTrashed).ToArray(), "Куда переместить группу генерации");
        if (choice is null) throw new OperationCanceledException();
        CheckManagementSnapshot(snapshot, token); await service.MoveMediaToCollectionAsync(snapshot.ProjectId, new[] { media.WorkflowId }, choice.Id, snapshot.Credential, token);
        CheckManagementSnapshot(snapshot, token); UpdateLocalWorkflowMetadata(media, collection: choice.Id ?? "");
    }, ct);

    void InitializeProjectManagementContextMenu()
    {
        projectList.PreviewMouseRightButtonDown += SelectManagementContextRow; projectList.ContextMenu = new ContextMenu();
        projectList.ContextMenuOpening += (_, args) =>
        {
            if (projectList.SelectedItem is not CloudProjectCard selected) { args.Handled = true; return; }
            var project = selected; var menu = projectList.ContextMenu; menu.Items.Clear();
            AddManagementMenuItem(menu, "Переименовать проект в Flow…", () => RunManagementOperationAsync(project.Id, async (service, snapshot, ct) =>
            {
                var title = RequestManagementText("Проект Flow", "Название проекта", project.Title);
                if (title is not null)
                {
                    CheckManagementSnapshot(snapshot, ct); await service.RenameProjectAsync(snapshot.ProjectId, title, snapshot.Credential, ct); CheckManagementSnapshot(snapshot, ct);
                    if (CloudProjectBox.Text.Trim().Replace("projects/", "") == snapshot.ProjectId && projectName == project.Title) { projectName = title; MarkDirty(); await SaveCurrentAsync(); }
                    await RefreshCloudLibraryAsync(ct);
                }
            }));
            AddManagementMenuItem(menu, "Сбросить обложку проекта", () => RunManagementOperationAsync(project.Id, async (service, snapshot, ct) => { await service.SetProjectCoverAsync(snapshot.ProjectId, null, snapshot.Credential, ct); await RefreshCloudLibraryAsync(ct); }));
        };
    }

    static void SelectManagementContextRow(object sender, MouseButtonEventArgs args)
    {
        if (sender is ListBox list && args.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem item) { item.IsSelected = true; item.Focus(); }
    }
    void AddManagementMenuItem(ContextMenu menu, string caption, Func<Task> action)
    {
        menu.Background = (Brush)FindResource("Panel"); menu.Foreground = (Brush)FindResource("Accent"); menu.BorderBrush = (Brush)FindResource("Line"); menu.Resources[typeof(MenuItem)] = CreateLibraryMenuItemStyle();
        var item = new MenuItem { Header = caption, IsEnabled = !closePending && managementTask?.IsCompleted != false && libraryActionTask?.IsCompleted != false };
        item.Click += async (_, _) => { try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { if (!closePending) Error(error); } }; menu.Items.Add(item);
    }

    ManagementSnapshot CaptureManagementSnapshot(string? projectId)
    {
        if (!HasFlowCredential) throw new InvalidOperationException("Сначала подключите аккаунт Google.");
        projectId ??= (managementProject?.SelectedItem as CloudProjectCard)?.Id;
        if (string.IsNullOrEmpty(projectId) || !cloudProjects.Any(project => project.Id == projectId)) throw new InvalidOperationException("Выберите проект активного аккаунта Flow.");
        return new(projectId, FlowCredential, accountRevision);
    }
    void CheckManagementSnapshot(ManagementSnapshot snapshot, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (closePending || snapshot.Revision != accountRevision) throw new OperationCanceledException(); }

    async Task RunManagementOperationAsync(string? projectId, Func<FlowManagementService, ManagementSnapshot, CancellationToken, Task> action, bool guardProject = true)
    {
        if (closePending || managementTask?.IsCompleted == false || libraryActionTask?.IsCompleted == false) return;
        var projectLock = false;
        CancellationTokenSource? cancellation = null; TaskCompletionSource? completion = null;
        try
        {
            if (guardProject && (generating || exporting || preparingGeneration || loginInProgress || !await projectOperation.WaitAsync(0, lifetime.Token))) throw new InvalidOperationException("Дождитесь завершения текущей задачи.");
            projectLock = guardProject;
            var snapshot = CaptureManagementSnapshot(projectId); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); managementCancellation = cancellation;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); managementTask = completion.Task;
            if (managementProject is not null) managementProject.IsEnabled = false;
            await action(new FlowManagementService(http), snapshot, cancellation.Token); CheckManagementSnapshot(snapshot, cancellation.Token);
        }
        catch (OperationCanceledException) { if (!closePending && managementStatus is not null) managementStatus.Text = "Ожидание действия остановлено. Обновите список для проверки состояния Flow."; }
        catch (Exception error) { if (!closePending) { if (managementStatus is not null) managementStatus.Text = error.Message; if (guardProject) Error(error); } }
        finally
        {
            if (managementProject is not null) managementProject.IsEnabled = true;
            if (projectLock) projectOperation.Release(); cancellation?.Dispose(); managementCancellation = null; completion?.TrySetResult();
        }
    }

    async Task ReadManagementStateAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    {
        managementStatus.Text = "Получаем коллекции и корзину Flow…";
        // Never leave project A's actionable collection rows under project B's selector
        // if the new project's read fails or is still in flight.
        managementCollections.Clear(); managementArchivedCollections.Clear(); managementTrashedMedia.Clear();
        var collectionsTask = service.ListCollectionsAsync(snapshot.ProjectId, snapshot.Credential, ct);
        var trashTask = service.ListTrashedMediaAsync(snapshot.ProjectId, snapshot.Credential, ct);
        await Task.WhenAll(collectionsTask, trashTask); CheckManagementSnapshot(snapshot, ct);
        var collections = await collectionsTask; var trashed = await trashTask;
        managementCollections.Clear(); managementArchivedCollections.Clear(); managementTrashedMedia.Clear();
        foreach (var item in collections.Where(value => !value.IsTrashed).OrderByDescending(value => value.IsFavorite).ThenBy(value => value.Title)) managementCollections.Add(item);
        foreach (var item in collections.Where(value => value.IsTrashed)) managementArchivedCollections.Add(item);
        foreach (var item in trashed.GroupBy(value => value.WorkflowId).Select(group => group.First())) managementTrashedMedia.Add(item);
        managementStatus.Text = $"Коллекций: {managementCollections.Count}. Групп в корзине: {managementTrashedMedia.Count}. Архивированных папок: {managementArchivedCollections.Count}.";
    }

    FlowCollectionInfo SelectedManagementCollection(ManagementSnapshot? snapshot = null)
    {
        var collection = managementCollectionList.SelectedItem as FlowCollectionInfo ?? throw new InvalidOperationException("Выберите коллекцию.");
        if (snapshot is not null && collection.ProjectId != snapshot.ProjectId) throw new InvalidOperationException("Обновите коллекции выбранного проекта.");
        return collection;
    }
    async Task CreateManagementCollectionAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    {
        var title = RequestManagementText("Новая коллекция Flow", "Название коллекции", "Новая коллекция", "Создать"); if (title is null) return;
        CheckManagementSnapshot(snapshot, ct); await service.CreateCollectionAsync(snapshot.ProjectId, title, null, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct);
    }
    async Task CreateManagementSubcollectionAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    {
        var parent = SelectedManagementCollection(snapshot); var title = RequestManagementText("Вложенная коллекция Flow", "Название коллекции внутри «" + parent.Title + "»", "Новая коллекция", "Создать"); if (title is null) return;
        CheckManagementSnapshot(snapshot, ct); await service.CreateCollectionAsync(snapshot.ProjectId, title, parent.Id, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct);
    }
    async Task RenameManagementCollectionAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    {
        var collection = SelectedManagementCollection(snapshot); var title = RequestManagementText("Название коллекции", "Новое название", collection.Title); if (title is null) return;
        CheckManagementSnapshot(snapshot, ct); await service.RenameCollectionAsync(snapshot.ProjectId, collection.Id, title, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct);
    }
    async Task FavoriteManagementCollectionAsync(FlowManagementService service, ManagementSnapshot snapshot, bool value, CancellationToken ct)
    { var collection = SelectedManagementCollection(snapshot); await service.SetCollectionFavoriteAsync(snapshot.ProjectId, collection.Id, value, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct); }
    async Task ArchiveManagementCollectionAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    { var collection = SelectedManagementCollection(snapshot); await service.SetCollectionTrashedAsync(snapshot.ProjectId, collection.Id, true, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct); }
    async Task RestoreManagementCollectionAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    { var collection = managementArchivedList.SelectedItem as FlowCollectionInfo ?? throw new InvalidOperationException("Выберите архивированную папку коллекции."); if (collection.ProjectId != snapshot.ProjectId) throw new InvalidOperationException("Обновите коллекции выбранного проекта."); await service.SetCollectionTrashedAsync(snapshot.ProjectId, collection.Id, false, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct); }
    async Task RestoreManagementMediaAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    { var media = managementTrashList.SelectedItem as FlowCloudMediaInfo ?? throw new InvalidOperationException("Выберите группу генерации в корзине."); if (media.ProjectId != snapshot.ProjectId) throw new InvalidOperationException("Обновите корзину выбранного проекта."); await service.SetMediaTrashedAsync(snapshot.ProjectId, media.WorkflowId, false, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct); await RefreshCloudLibraryAsync(ct); }
    async Task MoveManagementCollectionAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    {
        var source = SelectedManagementCollection(snapshot);
        var allowed = managementCollections.Where(value => value.Id != source.Id && !IsManagementDescendant(value, source.Id)).ToArray();
        var choice = PickManagementCollection(allowed, "Куда переместить коллекцию"); if (choice is null) return;
        CheckManagementSnapshot(snapshot, ct); await service.MoveCollectionAsync(snapshot.ProjectId, source.Id, choice.Id, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct);
    }
    bool IsManagementDescendant(FlowCollectionInfo collection, string sourceId)
    {
        var parent = collection.ParentId; var seen = new HashSet<string>();
        while (!string.IsNullOrEmpty(parent) && seen.Add(parent)) { if (parent == sourceId) return true; parent = managementCollections.FirstOrDefault(value => value.Id == parent)?.ParentId ?? ""; }
        return false;
    }
    async Task SetManagementCollectionCoverAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    {
        var collection = SelectedManagementCollection(snapshot); var asset = selectedAsset;
        if (asset is null || asset.CloudProjectId != snapshot.ProjectId || string.IsNullOrEmpty(asset.CloudMediaName)) throw new InvalidOperationException("Сначала выберите материал этого проекта в медиатеке.");
        await service.SetCollectionCoverAsync(snapshot.ProjectId, collection.Id, asset.CloudMediaName, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct);
    }
    async Task ClearManagementCollectionCoverAsync(FlowManagementService service, ManagementSnapshot snapshot, CancellationToken ct)
    { var collection = SelectedManagementCollection(snapshot); await service.SetCollectionCoverAsync(snapshot.ProjectId, collection.Id, null, snapshot.Credential, ct); await ReadManagementStateAsync(service, snapshot, ct); }

    Task OpenManagementCollectionAsync()
    {
        var collection = SelectedManagementCollection();
        libraryCollectionFilterProject = collection.ProjectId; libraryCollectionFilterId = collection.Id; libraryCollectionFilterTitle = collection.Title;
        libraryScope.SelectedIndex = 0; RefreshLibraryScope(); SwitchPage("library"); ApplyLibraryView(); return Task.CompletedTask;
    }

    CollectionChoice? PickManagementCollection(IReadOnlyList<FlowCollectionInfo> collections, string caption)
    {
        var choices = new List<CollectionChoice> { new(null, "В корень проекта") };
        choices.AddRange(collections.Select(value => new CollectionChoice(value.Id, value.Title)));
        var list = new ListBox { ItemsSource = choices, DisplayMemberPath = "Label", MinWidth = 390, Height = 240, SelectedIndex = 0 };
        var button = new Button { Content = "Переместить", Style = (Style)FindResource("AccentButton"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        var body = new StackPanel { Margin = new Thickness(24) }; body.Children.Add(new TextBlock { Text = caption, FontSize = 17, Margin = new Thickness(0, 0, 0, 14) }); body.Children.Add(list); body.Children.Add(button);
        var dialog = new Window { Title = caption, Owner = this, Content = body, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Style = (Style)FindResource(typeof(Window)) };
        button.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? list.SelectedItem as CollectionChoice : null;
    }
    string? RequestManagementText(string title, string caption, string initial, string buttonCaption = "Сохранить")
    {
        var box = new TextBox { Text = initial, MinWidth = 360, MaxLength = 200, Margin = new Thickness(0, 12, 0, 16) };
        var button = new Button { Content = buttonCaption, Style = (Style)FindResource("AccentButton"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var body = new StackPanel { Margin = new Thickness(24) }; body.Children.Add(new TextBlock { Text = caption, FontSize = 17 }); body.Children.Add(box); body.Children.Add(button);
        var dialog = new Window { Title = title, Owner = this, Content = body, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Style = (Style)FindResource(typeof(Window)) };
        button.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(box.Text)) dialog.DialogResult = true; }; dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }

    async Task CancelManagementUiAsync()
    {
        managementCancellation?.Cancel(); if (managementTask is { } task) await task;
        managementCollections.Clear(); managementArchivedCollections.Clear(); managementTrashedMedia.Clear();
        if (managementProject is not null) { managementInitializing = true; managementProject.SelectedItem = null; managementInitializing = false; }
    }
}
