using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlowStudio.Services;
public sealed record FlowAgentMediaCard(string MediaId,string Caption,string Kind);
public sealed record FlowAgentDisplay(string Text,IReadOnlyList<FlowAgentMediaCard> Media);
public static class FlowAgentPresentation
{
    static readonly Regex CodeBlocks=new(@"```(?:json)?[^\S\r\n]*\r?\n(?<json>[\s\S]*?)```",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(300));
    public static FlowAgentDisplay Parse(string text)
    {
        if(text.Length>1024*1024)return new(text,[]);
        var cards=new Dictionary<string,FlowAgentMediaCard>(StringComparer.Ordinal);
        try
        {
            var clean=CodeBlocks.Replace(text,match=>
            {
                try
                {
                    using var json=JsonDocument.Parse(match.Groups["json"].Value,new JsonDocumentOptions{MaxDepth=40});
                    if(json.RootElement.ValueKind!=JsonValueKind.Array||!json.RootElement.EnumerateArray().Any(e=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty("surfaceUpdate",out _)))return match.Value;
                    var before=cards.Count;
                    var paragraphs=new List<string>();
                    string Literal(JsonElement value)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty("literalString",out var literal)&&literal.ValueKind==JsonValueKind.String?literal.GetString()??"":"";
                    void Visit(JsonElement value)
                    {
                        if(cards.Count>=32)return;
                        if(value.ValueKind==JsonValueKind.Array){foreach(var child in value.EnumerateArray())Visit(child);return;}
                        if(value.ValueKind!=JsonValueKind.Object)return;
                        if(value.TryGetProperty("Text",out var textComponent)&&textComponent.ValueKind==JsonValueKind.Object&&textComponent.TryGetProperty("text",out var textValue))
                        {
                            var paragraph=Literal(textValue);
                            if(paragraph.Contains("\\n\\n",StringComparison.Ordinal))paragraph=paragraph.Replace("\\n","\n",StringComparison.Ordinal);
                            if(!string.IsNullOrWhiteSpace(paragraph))paragraphs.Add(paragraph);
                        }
                        foreach(var kind in new[]{"video","image"})
                            if(value.TryGetProperty(kind,out var media))
                            {
                                var name=Literal(media);if(!Guid.TryParse(name.Replace("media/","",StringComparison.Ordinal),out var id))continue;
                                var caption=value.TryGetProperty("description",out var description)?Literal(description):"";
                                if(caption.Length==0&&value.TryGetProperty("label",out var label))caption=Literal(label);
                                cards[id.ToString()]=new(id.ToString(),caption.Length>500?caption[..500]:caption,kind);
                            }
                        foreach(var child in value.EnumerateObject())Visit(child.Value);
                    }
                    Visit(json.RootElement);
                    var message=string.Join("\n\n",paragraphs.Distinct(StringComparer.Ordinal));
                    if(cards.Count>before)message+=(message.Length>0?"\n\n":"")+"[Результаты Flow — в карточках ниже]";
                    return message.Length>0?message:"[Интерактивный блок Flow]";
                }
                catch(JsonException){return match.Value;}
            });
            return new(clean.Trim(),cards.Values.ToArray());
        }
        catch(RegexMatchTimeoutException){return new(text,[]);}
    }
    public static Task<string> SelfTestAsync()
    {
        const string id="9db3b29a-009b-4e24-a0d9-39209b1a2739";
        var payload="```json\n[{\"surfaceUpdate\":{\"components\":[{\"component\":{\"MultipleChoice\":{\"options\":[{\"video\":{\"literalString\":\""+id+"\"},\"description\":{\"literalString\":\"Scene 1\"}}]}}}]}}]\n```\nГотово";
        var display=Parse(payload);if(display.Media.Count!=1||display.Media[0].MediaId!=id||display.Media[0].Caption!="Scene 1"||display.Text.Contains("surfaceUpdate")||!display.Text.Contains("Готово"))throw new InvalidOperationException("Agent media presentation failed.");
        if(Parse("```json\n{\"ordinary\":true}\n```").Text.Contains("Интерактивный"))throw new InvalidOperationException("Ordinary JSON was changed.");
        var textBlock="```json\n[{\"surfaceUpdate\":{\"components\":[{\"component\":{\"Text\":{\"text\":{\"literalString\":\"Три сцены готовы.\\\\n\\\\nКаждая по 10 секунд.\"}}}}]}}]\n```";
        var textDisplay=Parse(textBlock);
        if(!textDisplay.Text.Contains("Три сцены готовы.\n\nКаждая по 10 секунд.")||textDisplay.Text.Contains("Интерактивный блок"))throw new InvalidOperationException("A2UI agent prose was hidden or newlines were lost.");
        return Task.FromResult("A2UI media cards and actual Text prose rendered without executing actions/URLs, ordinary code preserved, bounded parsing.");
    }
}
