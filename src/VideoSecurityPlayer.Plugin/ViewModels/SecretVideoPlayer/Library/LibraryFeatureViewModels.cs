namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Library;

/// <summary>目录打开、扫描代次、监听批次和扫描状态的功能切片。</summary>
public sealed class LibraryCatalogViewModel(LibraryBrowserCoordinatorViewModel owner)
{
    public LibraryBrowserCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}

/// <summary>搜索、排序、筛选、选择和可见集合投影的功能切片。</summary>
public sealed class LibraryQueryViewModel(LibraryBrowserCoordinatorViewModel owner)
{
    public LibraryBrowserCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}

/// <summary>密码、显式激活、相邻项导航和连续播放的功能切片。</summary>
public sealed class LibraryPlaybackViewModel(LibraryDocumentCoordinatorViewModel owner)
{
    public LibraryDocumentCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}

/// <summary>单项历史、全部历史和清除确认状态的功能切片。</summary>
public sealed class LibraryHistoryViewModel(LibraryDocumentCoordinatorViewModel owner)
{
    public LibraryDocumentCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}

/// <summary>侧栏与渐进式设置面板布局偏好的功能切片。</summary>
public sealed class LibraryLayoutViewModel(LibraryDocumentCoordinatorViewModel owner)
{
    public LibraryDocumentCoordinatorViewModel Owner { get; } =
        owner ?? throw new ArgumentNullException(nameof(owner));
}
