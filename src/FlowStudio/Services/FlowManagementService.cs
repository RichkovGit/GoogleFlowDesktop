using System.Net.Http;
using System.Text.Json;

namespace FlowStudio.Services;

public sealed record FlowCollectionInfo(string Id, string ProjectId, string ParentId, string Title,
    bool IsFavorite, bool IsTrashed, string? ThumbnailMediaName, DateTimeOffset? CreatedAt, DateTimeOffset? TrashedAt);

/// <summary>
/// Native, masked project/workflow/collection mutations recovered from the public Flow frontend.
/// No mutation is retried. Permanent deletion is available only as an explicit method.
/// Source contracts and unsupported actions: research/flow_management_contract.md.
/// </summary>
public sealed class FlowManagementService(HttpClient api)
{
    const string Service="google.internal.labs.aisandbox.proto.flow.v1.FlowService";
    const string Sandbox="google.internal.labs.aisandbox.v1.AiSandbox";

    public Task RenameProjectAsync(string projectId,string title,string credential,CancellationToken ct=default) =>
        ProjectInfoAsync(projectId,Message((1,Title(title))),["project_title"],credential,ct);

    public Task SetProjectCoverAsync(string projectId,string? mediaName,string credential,CancellationToken ct=default)
    {
        var name=string.IsNullOrEmpty(mediaName)?"":Identifier(mediaName,"материала");
        return ProjectInfoAsync(projectId,Message((2,name),(3,name.Length>0)),["thumbnail_media_key","is_thumbnail_set_by_user"],credential,ct);
    }

    public Task RenameMediaAsync(string projectId,string workflowId,string title,string credential,CancellationToken ct=default) =>
        WorkflowMetadataAsync(projectId,workflowId,Message((1,Title(title))),"metadata.display_name",credential,ct);

    public Task SetFavoriteAsync(string projectId,string workflowId,bool value,string credential,CancellationToken ct=default) =>
        WorkflowMetadataAsync(projectId,workflowId,Message((4,value)),"metadata.favorited",credential,ct);

    public Task SetMediaTrashedAsync(string projectId,string workflowId,bool value,string credential,CancellationToken ct=default) =>
        WorkflowMetadataAsync(projectId,workflowId,Message((3,value)),"metadata.archived",credential,ct);

    public async Task<IReadOnlyList<FlowCloudMediaInfo>> ListTrashedMediaAsync(string projectId,string credential,CancellationToken ct=default)
    {
        projectId=Project(projectId);
        var contents=await FlowNativeRpc.SendAsync(api,Service,"GetProjectContents",["projects/"+projectId],credential,ct);
        return FlowCloudLibraryService.ParseMedia(contents,projectId,includeTrashed:true).Where(m=>m.IsTrashed).ToArray();
    }

    public async Task<IReadOnlyList<FlowCollectionInfo>> ListCollectionsAsync(string projectId,string credential,CancellationToken ct=default)
    {
        projectId=Project(projectId);
        // The web grid reads collections from JS/GetProjectContents field1, not the unused ListCollections stub.
        var contents=await FlowNativeRpc.SendAsync(api,Service,"GetProjectContents",["projects/"+projectId],credential,ct);
        var collections=FlowNativeRpc.Repeated(contents,1).Select(c=>Collection(c,projectId)).DistinctBy(c=>c.Id,StringComparer.Ordinal).ToArray();
        if(collections.Length>50000)throw new FlowCloudLibraryException("Проект Flow содержит слишком много коллекций.");
        return collections.OrderByDescending(c=>c.CreatedAt).ToArray();
    }

    public async Task<FlowCollectionInfo> CreateCollectionAsync(string projectId,string title,string? parentCollectionId,string credential,CancellationToken ct=default)
    {
        projectId=Project(projectId);
        var id=Guid.NewGuid().ToString();
        var collection=Message((1,id),(2,OptionalId(parentCollectionId)),(3,Message((1,Title(title)))),(4,projectId));
        var result=await FlowNativeRpc.SendAsync(api,Service,"CreateCollection",["projects/"+projectId,collection],credential,ct);
        var created=Collection(result,projectId);
        if(created.Id!=id)throw new FlowCloudLibraryException("Flow вернул неожиданный идентификатор новой коллекции.");
        return created;
    }

    public Task RenameCollectionAsync(string projectId,string collectionId,string title,string credential,CancellationToken ct=default) =>
        CollectionMetadataAsync(projectId,collectionId,Message((1,Title(title))),"metadata.display_name",credential,ct);

    public Task SetCollectionFavoriteAsync(string projectId,string collectionId,bool value,string credential,CancellationToken ct=default) =>
        CollectionMetadataAsync(projectId,collectionId,Message((5,value)),"metadata.favorited",credential,ct);

    public Task SetCollectionTrashedAsync(string projectId,string collectionId,bool value,string credential,CancellationToken ct=default) =>
        CollectionMetadataAsync(projectId,collectionId,Message((3,value)),"metadata.archived",credential,ct);

    public Task SetCollectionCoverAsync(string projectId,string collectionId,string? mediaName,string credential,CancellationToken ct=default) =>
        CollectionMetadataAsync(projectId,collectionId,Message((2,OptionalId(mediaName))),"metadata.thumbnail_media_key",credential,ct);

    public Task MoveCollectionAsync(string projectId,string collectionId,string? parentCollectionId,string credential,CancellationToken ct=default)
    {
        projectId=Project(projectId);collectionId=Identifier(collectionId,"коллекции");
        var parent=OptionalId(parentCollectionId);
        if(parent==collectionId)throw new ArgumentException("Коллекцию нельзя поместить внутрь себя.");
        return Mutate(Service,"UpdateCollection",[Message((1,collectionId),(2,parent),(4,projectId)),Mask("parent_collection_id")],credential,ct);
    }

    public Task MoveMediaToCollectionAsync(string projectId,IReadOnlyList<string> workflowIds,string? collectionId,string credential,CancellationToken ct=default)
    {
        projectId=Project(projectId);
        var ids=Identifiers(workflowIds);
        var destination=OptionalId(collectionId);
        // Ibb: project1, destination collection2/name1 or root6/empty, workflow ids3.
        var body=destination.Length>0?Message((1,projectId),(2,Message((1,destination))),(3,ids)):
            Message((1,projectId),(3,ids),(6,Array.Empty<object?>()));
        return Mutate(Service,"BatchMoveToCollection",body,credential,ct);
    }

    public Task PermanentlyDeleteMediaAsync(string projectId,IReadOnlyList<string> workflowIds,string credential,CancellationToken ct=default) =>
        Mutate(Service,"BatchDeleteAssets",Message((2,Identifiers(workflowIds)),(3,Project(projectId))),credential,ct);

    public Task PermanentlyDeleteCollectionsAsync(string projectId,IReadOnlyList<string> collectionIds,string credential,CancellationToken ct=default) =>
        Mutate(Service,"BatchDeleteAssets",Message((1,Identifiers(collectionIds)),(3,Project(projectId))),credential,ct);

    public Task PermanentlyDeleteProjectAsync(string projectId,string credential,CancellationToken ct=default) =>
        Mutate(Sandbox,"DeleteProject",["projects/"+Project(projectId)],credential,ct);

    Task ProjectInfoAsync(string projectId,object?[] info,string[] paths,string credential,CancellationToken ct) =>
        // CM.name1, projectInfo2, mask3, tM client context4/tool2=22.
        Mutate(Sandbox,"UpdateProjectInfo",["projects/"+Project(projectId),info,new object?[]{paths},Message((2,22))],credential,ct);

    Task WorkflowMetadataAsync(string projectId,string workflowId,object?[] metadata,string path,string credential,CancellationToken ct) =>
        Mutate(Service,"UpdateWorkflow",[Message((1,Identifier(workflowId,"рабочего процесса")),(4,metadata),(5,Project(projectId))),Mask(path)],credential,ct);

    Task CollectionMetadataAsync(string projectId,string collectionId,object?[] metadata,string path,string credential,CancellationToken ct) =>
        Mutate(Service,"UpdateCollection",[Message((1,Identifier(collectionId,"коллекции")),(3,metadata),(4,Project(projectId))),Mask(path)],credential,ct);

    async Task Mutate(string service,string method,object?[] body,string credential,CancellationToken ct) =>
        _=await FlowNativeRpc.SendAsync(api,service,method,body,credential,ct);

    internal static FlowCollectionInfo Collection(JsonElement value,string projectId)
    {
        var id=Identifier(FlowNativeRpc.Text(value,1)??"","коллекции");
        var owner=FlowNativeRpc.Text(value,4);
        if(!string.IsNullOrEmpty(owner)&&Project(owner)!=projectId)throw new FlowCloudLibraryException("Flow вернул коллекцию другого проекта.");
        var metadata=FlowNativeRpc.Field(value,3);
        return new(id,projectId,FlowNativeRpc.Text(value,2)??"",FlowNativeRpc.Text(metadata,1)??"Без названия",
            FlowNativeRpc.Boolean(metadata,5),FlowNativeRpc.Boolean(metadata,3),FlowNativeRpc.Text(metadata,2),
            FlowNativeRpc.Timestamp(FlowNativeRpc.Field(metadata,4)),FlowNativeRpc.Timestamp(FlowNativeRpc.Field(metadata,6)));
    }

    static object?[] Mask(string path)=>new object?[]{new[]{path}};
    static string Project(string value)=>FlowCloudLibraryService.NormalizeProjectId(value);
    static string Title(string value)
    {
        value=value.Trim();
        if(value.Length is <1 or >200||value.Any(char.IsControl))throw new ArgumentException("Название должно содержать от 1 до 200 символов без управляющих знаков.");
        return value;
    }
    static string OptionalId(string? value)=>string.IsNullOrWhiteSpace(value)?"":Identifier(value,"объекта");
    static string Identifier(string value,string kind)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>2048||value.Any(char.IsControl)||value.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Некорректный идентификатор {kind} Flow.");
        return value;
    }
    static string[] Identifiers(IReadOnlyList<string> values)
    {
        if(values.Count is <1 or >1000)throw new ArgumentException("Выберите от 1 до 1000 объектов Flow.");
        return values.Select(v=>Identifier(v,"объекта")).Distinct(StringComparer.Ordinal).ToArray();
    }
    static object?[] Message(params (int Field,object? Value)[] fields)
    {
        var values=new object?[fields.Max(f=>f.Field)];
        foreach(var (field,value) in fields)values[field-1]=value;
        return values;
    }
}
