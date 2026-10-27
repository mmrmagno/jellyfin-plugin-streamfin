using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace StreamFin;

// One tree, two encodings. Subsonic XML puts scalars in attributes and lists in repeated elements;
// its JSON turns attributes into fields and repeated elements into arrays, named like the element.
public sealed class SubsonicNode(string name)
{
    public string Name { get; } = name;
    public List<(string Key, object Value)> Fields { get; } = [];
    public List<(SubsonicNode Node, bool Many)> Children { get; } = [];
    // XML text content, JSON "value" (lyrics).
    public string? Text { get; set; }

    public SubsonicNode Set(string key, object? value)
    {
        if (value is not null && value is not string { Length: 0 }) Fields.Add((key, value));
        return this;
    }
    public SubsonicNode Add(SubsonicNode child) { Children.Add((child, true)); return this; }
    public SubsonicNode AddRange(IEnumerable<SubsonicNode> children) { foreach (var child in children) Add(child); return this; }
    public SubsonicNode One(SubsonicNode child) { Children.Add((child, false)); return this; }
}

public static class SubsonicResponse
{
    public const string ApiVersion = "1.16.1";
    private const string Namespace = "http://subsonic.org/restapi";

    public static SubsonicNode Root(bool ok) => new SubsonicNode("subsonic-response").Set("status", ok ? "ok" : "failed").Set("version", ApiVersion)
        .Set("type", "streamfin").Set("serverVersion", typeof(SubsonicResponse).Assembly.GetName().Version?.ToString() ?? "0").Set("openSubsonic", true);

    public static SubsonicNode Error(int code, string message) => Root(false).One(new SubsonicNode("error").Set("code", code).Set("message", message));

    private static string Scalar(object value) => value switch
    {
        bool b => b ? "true" : "false",
        DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    public static byte[] Xml(SubsonicNode root)
    {
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false }))
        {
            void Write(SubsonicNode node)
            {
                writer.WriteStartElement(node.Name, Namespace);
                foreach (var (key, value) in node.Fields.Where(f => f.Value is not System.Collections.IEnumerable || f.Value is string))
                    writer.WriteAttributeString(key, Clean(Scalar(value)));
                foreach (var (key, value) in node.Fields.Where(f => f.Value is System.Collections.IEnumerable and not string))
                    foreach (var item in (System.Collections.IEnumerable)value) writer.WriteElementString(key, Namespace, Clean(Scalar(item)));
                foreach (var (child, _) in node.Children) Write(child);
                if (node.Text is not null) writer.WriteString(Clean(node.Text));
                writer.WriteEndElement();
            }
            Write(root);
        }
        return output.ToArray();
    }

    // Characters XML 1.0 cannot carry at all; catalog text should never have them, but a parser must not fail on one.
    private static string Clean(string text) => text.Any(c => c < 0x20 && c is not ('\t' or '\n' or '\r') || c is '￾' or '￿')
        ? new string(text.Where(c => !(c < 0x20 && c is not ('\t' or '\n' or '\r')) && c is not ('￾' or '￿')).ToArray()) : text;

    public static byte[] Json(SubsonicNode root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            void Value(object value)
            {
                switch (value)
                {
                    case bool b: writer.WriteBooleanValue(b); break;
                    case int or long or double or float or decimal: writer.WriteRawValue(Scalar(value)); break;
                    case string s: writer.WriteStringValue(s); break;
                    case System.Collections.IEnumerable list:
                        writer.WriteStartArray();
                        foreach (var item in list) Value(item);
                        writer.WriteEndArray();
                        break;
                    default: writer.WriteStringValue(Scalar(value)); break;
                }
            }
            void Body(SubsonicNode node)
            {
                writer.WriteStartObject();
                foreach (var (key, value) in node.Fields)
                {
                    writer.WritePropertyName(key);
                    Value(value);
                }
                foreach (var group in node.Children.GroupBy(c => c.Node.Name))
                {
                    writer.WritePropertyName(group.Key);
                    if (group.First().Many)
                    {
                        writer.WriteStartArray();
                        foreach (var (child, _) in group) Body(child);
                        writer.WriteEndArray();
                    }
                    else Body(group.First().Node);
                }
                if (node.Text is not null) writer.WriteString("value", node.Text);
                writer.WriteEndObject();
            }
            writer.WriteStartObject();
            writer.WritePropertyName(root.Name);
            Body(root);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }
}
