using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Observation;

namespace Spyglass.Proxy.Tests;

/// <summary>01 §9 at the <see cref="Redactor"/> level (observation review: PO-1, PO-4, PO-5, PO-19).</summary>
public sealed class RedactionPolicyTests
{
    private static BsonDocument Redact(BsonDocument doc, ValueMode mode = ValueMode.Truncated, int truncateAt = 16)
    {
        var r = new Redactor(new CaptureConfig { Values = mode, TruncateStringsAt = truncateAt });
        var (bson, _, _) = r.Redact(doc.GetElement(0).Name, doc.ToBson(), isHello: false, captureTruncated: false);
        return BsonSerializer.Deserialize<BsonDocument>(bson);
    }

    [Theory]
    [InlineData(ValueMode.Full)]
    [InlineData(ValueMode.Truncated)]
    [InlineData(ValueMode.Shape)]
    public void First_element_with_a_document_value_is_redacted_like_any_other(ValueMode mode)
    {
        var doc = Redact(new BsonDocument
        {
            ["explain"] = new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument { ["password"] = "hunter2", ["ssn"] = "123-45-6789" }, ["comment"] = new string('x', 200) },
            ["$db"] = "app",
        }, mode);
        var explain = doc["explain"].AsBsonDocument; // the command name survives
        Assert.Equal("$redacted", explain["filter"]["password"].AsString);
        Assert.Equal("$redacted", explain["filter"]["ssn"].AsString);
        Assert.DoesNotContain("hunter2", doc.ToJson());
        Assert.DoesNotContain("123-45-6789", doc.ToJson());
        if (mode == ValueMode.Shape) Assert.Equal("?string", explain["comment"].AsString);
        if (mode == ValueMode.Truncated) Assert.True(explain["comment"].AsString.Length < 40);
        Assert.Equal("app", doc["$db"].AsString);
    }

    [Fact]
    public void First_element_scalars_stay_verbatim_even_in_shape_mode()
    {
        var doc = Redact(new BsonDocument { ["find"] = "a-rather-long-collection-name", ["filter"] = new BsonDocument("a", 1), ["$db"] = "app" }, ValueMode.Shape);
        Assert.Equal("a-rather-long-collection-name", doc["find"].AsString);
        Assert.Equal("?number", doc["filter"]["a"].AsString);
        var getMore = Redact(new BsonDocument { ["getMore"] = 12345678901L, ["collection"] = "c" }, ValueMode.Shape);
        Assert.Equal(12345678901L, getMore["getMore"].AsInt64);
    }

    [Fact]
    public void Array_first_element_is_redacted()
    {
        var doc = Redact(new BsonDocument { ["applyOps"] = new BsonArray { new BsonDocument { ["op"] = "i", ["o"] = new BsonDocument("password", "hunter2") } } });
        Assert.DoesNotContain("hunter2", doc.ToJson());
    }

    [Fact]
    public void Code_with_scope_walks_the_scope_and_cuts_the_code()
    {
        var code = "this.x == s // " + new string('c', 100);
        var doc = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument("$where", new BsonJavaScriptWithScope(code, new BsonDocument { ["password"] = "hunter2", ["n"] = 1 })),
        });
        var where = doc["filter"]["$where"];
        Assert.Equal(BsonType.JavaScriptWithScope, where.BsonType); // still the original type
        var cws = where.AsBsonJavaScriptWithScope;
        Assert.Equal("$redacted", cws.Scope["password"].AsString);
        Assert.Equal(1, cws.Scope["n"].AsInt32);
        Assert.StartsWith("this.x == s // ", cws.Code);
        Assert.Contains("…(+", cws.Code);
        Assert.DoesNotContain("hunter2", doc.ToJson());

        // shape: a leaf tag, the scope is never exported
        var shaped = Redact(new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument("$where", new BsonJavaScriptWithScope(code, new BsonDocument("password", "hunter2"))) }, ValueMode.Shape);
        Assert.Equal("?other", shaped["filter"]["$where"].AsString);
    }

    [Fact]
    public void Javascript_and_symbol_strings_are_truncated()
    {
        var doc = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["a"] = new BsonJavaScript(new string('j', 100)),
            ["b"] = BsonSymbolTable.Lookup(new string('s', 100)),
        });
        Assert.Equal(BsonType.JavaScript, doc["a"].BsonType);
        Assert.True(doc["a"].AsBsonJavaScript.Code.Length < 40);
        Assert.Equal(BsonType.Symbol, doc["b"].BsonType);
        Assert.True(doc["b"].AsBsonSymbol.Name.Length < 40);
    }

    [Theory]
    [InlineData("newPassword")]
    [InlineData("ldapQueryPassword")]
    [InlineData("accessToken")]
    [InlineData("refresh_token")]
    [InlineData("clientSecret")]
    [InlineData("privateKey")]
    [InlineData("X-Api-Key")]
    [InlineData("apikey")]
    [InlineData("userCredentials")]
    [InlineData("passwd")]
    [InlineData("pass")]
    [InlineData("ssn")]
    public void Default_pattern_matches_names_that_contain_a_secret_word(string field)
    {
        var doc = Redact(new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument(field, "s3cr3t") });
        Assert.Equal("$redacted", doc["filter"][field].AsString);
    }

    [Theory]
    [InlineData("classname")]
    [InlineData("passenger")]
    [InlineData("compass")]
    [InlineData("discard")]
    [InlineData("monkey")]
    [InlineData("username")]
    public void Default_pattern_leaves_ordinary_names_alone(string field)
    {
        var doc = Redact(new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument(field, "v") });
        Assert.Equal("v", doc["filter"][field].AsString);
    }

    [Fact]
    public void Literals_compared_with_a_secret_field_path_are_redacted()
    {
        var doc = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$password", "hunter2" })),
        });
        Assert.DoesNotContain("hunter2", doc.ToJson());
        Assert.Equal("$password", doc["filter"]["$expr"]["$eq"][0].AsString); // the path stays: it names a field, not a value

        var nested = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$profile.apiKey", "k-123" })),
        });
        Assert.DoesNotContain("k-123", nested.ToJson());

        var plain = Redact(new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$name", "bob" })) });
        Assert.Equal("bob", plain["filter"]["$expr"]["$eq"][1].AsString);
    }

    [Fact]
    public void Errmsg_scrub_handles_values_of_every_shape()
    {
        var r = new Redactor(new CaptureConfig { Values = ValueMode.Full });
        string Scrub(string m) => System.Text.Encoding.UTF8.GetString(r.ScrubErrmsg(System.Text.Encoding.UTF8.GetBytes(m)));
        Assert.Equal("E11000 dup key: { token: \"$redacted\" }", Scrub("E11000 dup key: { token: \"abc, \\\"def\\\" }\" }"));
        Assert.Equal("dup key: { password: \"$redacted\", n: 1 }", Scrub("dup key: { password: 12345, n: 1 }"));
        Assert.Equal("dup key: { secret: \"$redacted\" }", Scrub("dup key: { secret: ObjectId('0123456789abcdef01234567') }"));
        Assert.Equal("cut: { apiKey: \"$redacted\"", Scrub("cut: { apiKey: \"sk_live_unterminated")); // a capture cut mid-value
        Assert.Equal("no pairs here", Scrub("no pairs here"));
        Assert.Equal("a: 1: b", Scrub("a: 1: b"));
    }
}
