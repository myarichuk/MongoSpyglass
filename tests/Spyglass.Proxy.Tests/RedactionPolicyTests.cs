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

    [Theory]
    [InlineData(ValueMode.Full)]
    [InlineData(ValueMode.Truncated)]
    public void Errmsg_scrub_closes_the_spelling_gaps_of_server_messages(ValueMode mode)
    {
        var r = new Redactor(new CaptureConfig { Values = mode });
        string Scrub(string m) => System.Text.Encoding.UTF8.GetString(r.ScrubErrmsg(System.Text.Encoding.UTF8.GetBytes(m)));

        // MongoDB < 4.2 leaves the key names out of dup key: the index name says which fields they are (FP-4)
        var legacy = Scrub("E11000 duplicate key error index: app.users.$apiKey_1 dup key: { : \"sk_live_123\" }");
        Assert.DoesNotContain("sk_live_123", legacy);
        Assert.Contains("index: app.users.$apiKey_1", legacy);
        Assert.Equal("E11000 duplicate key error index: app.users.$name_1 dup key: { : \"bob\" }",
            Scrub("E11000 duplicate key error index: app.users.$name_1 dup key: { : \"bob\" }")); // not a secret index: untouched

        // extended-JSON style: the key is a quoted token
        var json = Scrub("Document failed: {\"password\": \"hunter2\", \"n\": 1}");
        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("\"n\": 1", json);
        Assert.DoesNotContain("hunter2", Scrub("Document failed: {'password': 'hunter2'}"));

        // quotes inside a braced value do not end it early: the rest of the document is still the value
        var braces = Scrub("Plan executor error :: token: { $gt: \"a}b\", leak: \"zzz\" } trailing");
        Assert.DoesNotContain("zzz", braces);
        Assert.DoesNotContain("a}b", braces);
        Assert.EndsWith("trailing", braces);

        // a value with spaces goes whole
        var spaced = Scrub("bad password: my pass phrase, n: 1");
        Assert.DoesNotContain("pass phrase", spaced);
        Assert.Contains("n: 1", spaced);
    }

    [Fact]
    public void Literals_next_to_a_secret_path_are_redacted_through_nested_arrays_and_operators()
    {
        // FP-5: the literal list is a nested array; the path is wrapped in an operator
        var nested = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument("$expr", new BsonDocument("$in", new BsonArray { "$password", new BsonArray { "hunter2", "letmein" } })),
        });
        Assert.DoesNotContain("hunter2", nested.ToJson());
        Assert.DoesNotContain("letmein", nested.ToJson());
        Assert.Equal("$password", nested["filter"]["$expr"]["$in"][0].AsString);

        var wrapped = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { new BsonDocument("$toLower", "$password"), "hunter2" })),
        });
        Assert.DoesNotContain("hunter2", wrapped.ToJson());

        // an unrelated clause of the same $and keeps its literals
        var and = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$password", "hunter2" }),
                new BsonDocument("$eq", new BsonArray { "$name", "bob" }),
            })),
        });
        Assert.DoesNotContain("hunter2", and.ToJson());
        Assert.Equal("bob", and["filter"]["$expr"]["$and"][1]["$eq"][1].AsString);
    }

    [Fact]
    public void Where_javascript_that_names_a_secret_field_is_redacted()
    {
        var doc = Redact(new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument("$where", "this.password == 'hunter2'") });
        Assert.DoesNotContain("hunter2", doc.ToJson());
        var ok = Redact(new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument("$where", "this.age > 18") });
        Assert.Equal("this.age > 18", ok["filter"]["$where"].AsString);
    }

    [Fact]
    public void Sort_projection_and_hint_values_are_directions_not_data()
    {
        // FP-16: tokenCount / password are field names there; -1 and 0 are the sort direction and the exclusion flag
        var doc = Redact(new BsonDocument
        {
            ["find"] = "users",
            ["filter"] = new BsonDocument("password", "hunter2"),
            ["sort"] = new BsonDocument("tokenCount", -1),
            ["projection"] = new BsonDocument { ["password"] = 0, ["secretLevel"] = true },
            ["hint"] = new BsonDocument("apiKey", 1),
        });
        Assert.Equal("$redacted", doc["filter"]["password"].AsString); // data is still redacted
        Assert.Equal(-1, doc["sort"]["tokenCount"].AsInt32);
        Assert.Equal(0, doc["projection"]["password"].AsInt32);
        Assert.True(doc["projection"]["secretLevel"].AsBoolean);
        Assert.Equal(1, doc["hint"]["apiKey"].AsInt32);

        var pipeline = Redact(new BsonDocument
        {
            ["aggregate"] = "users",
            ["pipeline"] = new BsonArray { new BsonDocument("$sort", new BsonDocument("tokenCount", -1)), new BsonDocument("$match", new BsonDocument("token", "t-1")) },
        });
        Assert.Equal(-1, pipeline["pipeline"][0]["$sort"]["tokenCount"].AsInt32);
        Assert.Equal("$redacted", pipeline["pipeline"][1]["$match"]["token"].AsString);
    }

    [Theory]
    [InlineData("userSSN")]
    [InlineData("customerSsn")]
    [InlineData("social_security_number")]
    [InlineData("creditCard")]
    [InlineData("credit_card_number")]
    [InlineData("cardNo")]
    [InlineData("cvc")]
    [InlineData("cardCVC")]
    [InlineData("bearer")]
    [InlineData("pin")]
    [InlineData("otp")]
    public void Default_pattern_matches_more_secret_spellings(string field)
    {
        var doc = Redact(new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument(field, "s3cr3t") });
        Assert.Equal("$redacted", doc["filter"][field].AsString);
    }

    [Theory]
    [InlineData("spinning")]
    [InlineData("author")]
    [InlineData("cardinality")]
    [InlineData("cardNotes")]
    [InlineData("jessnr")]
    public void Default_pattern_leaves_lookalikes_alone(string field)
    {
        var doc = Redact(new BsonDocument { ["find"] = "users", ["filter"] = new BsonDocument(field, "v") });
        Assert.Equal("v", doc["filter"][field].AsString);
    }
}
