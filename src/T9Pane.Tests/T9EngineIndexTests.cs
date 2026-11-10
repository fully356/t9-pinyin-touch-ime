using T9Pane.Services;

namespace T9Pane.Tests;

/// <summary>
/// 阶段①：词库别码规范化与索引前置的引擎级回归。
/// 复用真实词库 fixture，验证「主码;别码」加载后不再污染拼音和数字码。
/// </summary>
public class T9EngineIndexTests : IClassFixture<T9EngineFixture>
{
    private readonly T9Engine _engine;

    public T9EngineIndexTests(T9EngineFixture fixture) => _engine = fixture.Engine;

    [Fact]
    public void Loaded_candidates_never_carry_semicolon_alias_in_pinyin()
    {
        foreach (var code in new[] { "2", "64", "64426", "96", "33" })
        {
            foreach (var hit in _engine.Query(code, 60))
            {
                Assert.DoesNotContain(';', hit.Pinyin);
            }
        }
    }

    [Fact]
    public void Semicolon_alias_character_is_reachable_on_its_primary_single_key()
    {
        // 「啊 a;a」主码是 a → 数字 2；别码 aa 不应把它推到 22。
        var single = _engine.Query("2", 200);
        Assert.Contains(single, hit => hit.Word == "啊");
    }

    [Fact]
    public void Letter_query_still_resolves_a_known_word_after_index_changes()
    {
        var nihao = _engine.QueryLetters("nihao", 60);
        Assert.Contains(nihao, hit => hit.Word == "你好");
        foreach (var hit in nihao)
        {
            Assert.DoesNotContain(';', hit.Pinyin);
        }
    }
}
