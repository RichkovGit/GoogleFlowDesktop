using System.Net.WebSockets;
using System.Text.Json;

namespace FlowStudio.Services;
public static class FlowBrowserReceiptSelfTest
{
    public static async Task<string> RunAsync(CancellationToken ct=default)
    {
        foreach(var fail in new[]{false,true})
        {
            using var socket=new FixtureSocket(fail);var connection=new FlowRuntimeCdpConnection(socket);
            await using var runtime=new FlowBrowserRuntimeService(connection);
            var denied=false;try{await runtime.ObserveActionAsync(ct);}catch(FlowBrowserRuntimeException ex){denied=ex.Message.Contains("код 7",StringComparison.Ordinal);}
            if(denied!=fail||socket.Calls.Count(c=>c=="Fetch.getResponseBody")!=1||socket.Calls.Count(c=>c=="Fetch.continueRequest")!=1)throw new InvalidOperationException("Browser action receipt was not drained exactly once or error was lost.");
        }
        return "Queued browser receipt drained during polling; accepted/denied results preserved; paused response released once. Fake WebSocket, no browser/network/credits.";
    }
    sealed class FixtureSocket(bool fail):WebSocket
    {
        readonly Queue<byte[]> responses=[];int offset;bool paused;
        public List<string> Calls {get;}=[];
        public override WebSocketCloseStatus? CloseStatus=>null;
        public override string? CloseStatusDescription=>null;
        public override WebSocketState State=>WebSocketState.Open;
        public override string? SubProtocol=>null;
        public override void Abort(){}
        public override void Dispose(){}
        public override Task CloseAsync(WebSocketCloseStatus s,string? d,CancellationToken ct)=>Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s,string? d,CancellationToken ct)=>Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> data,WebSocketMessageType type,bool end,CancellationToken ct)
        {
            using var json=JsonDocument.Parse(data.AsMemory());var root=json.RootElement;var id=root.GetProperty("id").GetInt32();var method=root.GetProperty("method").GetString()!;Calls.Add(method);
            object result=new{};
            if(method=="Runtime.evaluate")
            {
                if(!paused){paused=true;responses.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new{method="Fetch.requestPaused",@params=new{requestId="receipt",request=new{url="https://flow.google.com/_/AiSandboxAngularFrontend/data/batchexecute?rpcids=YhhmEf"},responseStatusCode=200}}));}
                result=new{result=new{type="string",value="true"}};
            }
            else if(method=="Fetch.getResponseBody")
            {
                var frame=fail?JsonSerializer.Serialize(new object?[]{new object?[]{"wrb.fr","YhhmEf",null,null,null,new object?[]{7,"denied"}}}):JsonSerializer.Serialize(new object?[]{new object?[]{"wrb.fr","YhhmEf","[]"}});
                result=new{body=")]}'\n\n"+(frame.Length+1)+"\n"+frame,base64Encoded=false};
            }
            responses.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new{id,result}));return Task.CompletedTask;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken ct)
        {
            var next=responses.Peek();var count=Math.Min(buffer.Count,next.Length-offset);next.AsSpan(offset,count).CopyTo(buffer.AsSpan());offset+=count;var done=offset==next.Length;if(done){responses.Dequeue();offset=0;}return Task.FromResult(new WebSocketReceiveResult(count,WebSocketMessageType.Text,done));
        }
    }
}
