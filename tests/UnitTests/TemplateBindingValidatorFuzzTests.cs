using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Seeded-RNG fuzz sweep for the deterministic template-binding validator (audit §4.5).
/// The hand-picked suites (TemplateBindingValidatorTests, PostExecuteVerifyTests) cover the
/// shapes we've already been bitten by — the #keywordsInput template-ref false positive and
/// the s.departure.ested chained-access hallucination — but only a generated sweep can lock
/// the whole SHAPE SPACE: random components and random templates through
/// ExtractComponentMembers / ExtractTemplateSymbols / ValidateTemplateBindings must (1) never
/// throw, (2) be deterministic, (3) never flag a DECLARED member regardless of binding shape,
/// (4) extract only ROOT symbols for member-access chains / pipes / loop locals / template
/// refs, and (5) still flag genuinely undeclared roots — the guard keeps its teeth.
/// Fixed seeds: any failure reproduces exactly.
/// </summary>
public class TemplateBindingValidatorFuzzTests
{
    private const int Seed = 20261022;

    // Distinctive words that are never Angular keywords, never $-prefixed, never controller
    // aliases — so any collision with the extractor's filter lists is a real finding.
    private static readonly string[] Words =
    {
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
        "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa",
        "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "yankee"
    };

    private static string Ident(Random rng) =>
        Words[rng.Next(Words.Length)] + (rng.Next(3) == 0 ? rng.Next(10).ToString() : "");

    private static string Prop(Random rng) => Ident(rng);

    // Member declaration shapes CollectMemberFromLine must recognize.
    private static string MemberDecl(Random rng, string name) => rng.Next(6) switch
    {
        0 => $"  {name} = 1;",
        1 => $"  {name}: string = 'x';",
        2 => $"  {name}() {{ return 1; }}",
        3 => $"  @Input() {name} = 2;",
        4 => $"  get {name}() {{ return 3; }}",
        _ => $"  private {name}: number = 4;",
    };

    private static string ComponentSource(Random rng, IReadOnlyList<string> members)
    {
        var body = string.Join("\n", members.Select(m => MemberDecl(rng, m)));
        return "import { Component, Input } from '@angular/core';\n\n" +
               "@Component({\n  selector: 'app-fuzz',\n  template: '<div>stub</div>'\n})\n" +
               "export class FuzzComponent {\n" + body + "\n}\n";
    }

    // Binding shapes that reference a ROOT symbol with an optional sub-property. Each entry
    // takes (root, prop) and returns an HTML fragment. All shapes are ones the extractor
    // recognizes (attribute bindings, events, two-way, interpolation, structural directives,
    // control flow, class/style, object literals, pipes, $event args).
    private static readonly (string shape, Func<string, string, string> build)[] Shapes =
    {
        ("[title]", (m, p) => $"<span [title]=\"{m}\">t</span>"),
        ("(click)()", (m, _) => $"<button (click)=\"{m}()\">c</button>"),
        ("[(ngModel)]", (m, p) => $"<input [(ngModel)]=\"{m}.{p}\">"),
        ("interpolation", (m, _) => $"<b>{{{{ {m} }}}}</b>"),
        ("*ngFor", (m, p) => $"<li *ngFor=\"let it of {m}.{p}\">{{{{ it }}}}</li>"),
        ("[class.x]", (m, _) => $"<div [class.active]=\"{m}\">x</div>"),
        ("[style.x]", (m, p) => $"<div [style.color]=\"{m}.{p}\">x</div>"),
        ("*ngIf", (m, _) => $"<div *ngIf=\"{m}\">x</div>"),
        ("@if", (m, p) => $"@if ({m}.{p}) {{ <span>y</span> }}"),
        ("@for", (m, p) => $"@for (let d of {m}.{p}; track d) {{ {{{{ d }}}}}}"),
        ("[ngClass] object", (m, p) => $"<span [ngClass]=\"{{ active: {m}.{p} }}\">x</span>"),
        ("pipe", (m, p) => $"<span>{{{{ {m}.{p} | uppercase }}}}</span>"),
        ("$event args", (m, p) => $"<span (keydown)=\"{m}($event, {m}.{p})\">x</span>"),
        ("let-of + as", (m, p) => $"<ul *ngFor=\"let it of {m}.{p} as itemsAlias\"><li>{{{{ it }}}}</li></ul>"),
    };

    private static string BuildTemplate(Random rng, IReadOnlyList<string> roots, int count)
    {
        var parts = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var (shapeName, build) = Shapes[rng.Next(Shapes.Length)];
            var root = roots[rng.Next(roots.Count)];
            parts.Add(build(root, Prop(rng)));
        }
        return string.Join("\n", parts);
    }

    // ── 1. Robustness + determinism ──────────────────────────────────────────────

    [Fact]
    public void GarbageAndStructuredTemplates_NeverThrow_AndAreDeterministic()
    {
        for (var i = 0; i < 300; i++)
        {
            var rng = FuzzHarness.SeededRng(Seed, i, 7919);
            var html = i switch
            {
                0 => "<div [x]=\"a (b\">unterminated attr",
                1 => "<p>{{ unclosed.interpolation",
                2 => "<!-- {{ z.w }} binding inside comment --><span [t]=\"k\"></span>",
                3 => "<script>function f() { return { a: 1 }; }</script><i [c]=\"d\"></i>",
                4 => "<style>.x { color: red; }</style><em [s]=\"v\"></em>",
                5 => "<input #r (keyup)=\"g(r.value)\"><label let-v=\"x\" [attr]=\"r.value\">",
                6 => "@for (let q of w; track q.id) { {{ q.name }",
                7 => "<div [a]=\"'q' + \\\"b\\\"\" [b]=\"x?.y!.z\">quotes and chaining</div>",
                _ => BuildTemplate(rng, new[] { Ident(rng), Ident(rng) }, rng.Next(1, 6)),
            };
            var component = i % 3 == 0
                ? ComponentSource(rng, new[] { Ident(rng), Ident(rng) })
                : "not a component at all";

            var s1 = TemplateBindingValidator.ExtractTemplateSymbols(html);
            var s2 = TemplateBindingValidator.ExtractTemplateSymbols(html);
            Assert.Equal(s1, s2); // no hidden mutable state between calls

            var m1 = TemplateBindingValidator.ExtractComponentMembers(component);
            var m2 = TemplateBindingValidator.ExtractComponentMembers(component);
            Assert.Equal(m1, m2);

            var v1 = TemplateBindingValidator.ValidateTemplateBindings("a.html", html, component);
            var v2 = TemplateBindingValidator.ValidateTemplateBindings("a.html", html, component);
            Assert.Equal(v1, v2);
        }
    }

    // ── 2. The false-positive lock: declared members are never flagged ───────────

    [Fact]
    public void DeclaredMembers_NeverFlagged_AcrossAllBindingShapes()
    {
        var exercisedShapes = new HashSet<string>();
        for (var i = 0; i < 120; i++)
        {
            var rng = FuzzHarness.SeededRng(Seed + 1, i, 104729);
            var members = Enumerable.Range(0, rng.Next(5, 11)).Select(_ => Ident(rng)).Distinct().ToList();
            var component = ComponentSource(rng, members);

            // The extractor must see every generated member — if a declaration shape is
            // missed, this fails loudly naming the member (extractor coverage lock).
            var extracted = TemplateBindingValidator.ExtractComponentMembers(component);
            foreach (var m in members)
                Assert.True(extracted.Contains(m),
                    $"ExtractComponentMembers missed '{m}' (decl shape: {MemberDecl(rng, m).Trim()})");

            // Reference EVERY member twice through random shapes.
            var allRefs = members.Concat(members).OrderBy(_ => rng.Next()).ToList();
            var html = string.Join("\n", allRefs.Select(m =>
            {
                var (shapeName, build) = Shapes[rng.Next(Shapes.Length)];
                exercisedShapes.Add(shapeName);
                return build(m, Prop(rng));
            }));

            var issues = TemplateBindingValidator.ValidateTemplateBindings("a.html", html, component);
            foreach (var m in members)
                Assert.True(!issues.Any(issue => issue.Contains($"'{m}'")),
                    $"declared member '{m}' was flagged:\n{string.Join("\n", issues)}");
        }
        FuzzHarness.AssertExercised(exercisedShapes.Count, "binding shapes");
        Assert.Equal(Shapes.Length, exercisedShapes.Count); // every shape hit at least once
    }

    // ── 3. The two named regressions, generalized: roots only, refs and pipes free ──

    [Fact]
    public void Chains_Pipes_TemplateRefs_LoopLocals_NeverProduceFalseSymbols()
    {
        // The s.departure.ested hallucination: chained access extracts the ROOT only.
        var component = ComponentSource(new Random(1), new[] { "s", "onKey" });
        var html = string.Join("\n",
            "<span>{{ s.departure.estimated }}</span>",
            "<span [val]=\"s.departure.ested\">x</span>",
            "<button (click)=\"s.departure.ested()\">c</button>",
            "<li *ngFor=\"let d of s.departure.list; let i = index\">{{ d.name }}{{ i }}</li>",
            "<span>{{ s.departure.arr | date:'HH:mm' }}</span>",
            "<span>{{ s | json }}</span>",
            "@for (let q of s.departures; track q.id) { {{ q.name }} }",
            "<input #keywordsInput (keyup)=\"onKey(keywordsInput.value)\">",
            "<div *ngIf=\"s.ready as readyFlag\">{{ readyFlag }}</div>");

        var symbols = TemplateBindingValidator.ExtractTemplateSymbols(html);
        Assert.Equal(new[] { "s", "onKey" }, symbols);

        var issues = TemplateBindingValidator.ValidateTemplateBindings("a.html", html, component);
        Assert.Empty(issues);

        // Seeded variations of the same shape space: chained roots declared, tails random.
        for (var i = 0; i < 80; i++)
        {
            var rng = FuzzHarness.SeededRng(Seed + 2, i, 15485863);
            var root = Ident(rng);
            var comp = ComponentSource(rng, new[] { root });
            var chains = string.Join("\n", Enumerable.Range(0, 6).Select(_ =>
            {
                var (shapeName, build) = Shapes[rng.Next(Shapes.Length)];
                return build(root, Prop(rng));
            }));
            // A template ref whose name collides with nothing declared must never flag.
            // (If it DID collide with the root, the ref would correctly shadow it — Angular
            // template scope — so the generator keeps them distinct.)
            var refName = Ident(rng);
            while (refName == root) refName = Ident(rng);
            var fullHtml = chains + $"\n<input #{refName}><span [t]=\"{refName}.value\"></span>";

            var sym = TemplateBindingValidator.ExtractTemplateSymbols(fullHtml);
            Assert.DoesNotContain(sym, x => x.Contains('.'));
            Assert.DoesNotContain(sym, x => x == refName);
            Assert.Contains(root, sym);

            var iss = TemplateBindingValidator.ValidateTemplateBindings("a.html", fullHtml, comp);
            Assert.True(iss.Count == 0,
                $"root '{root}' flagged through chains/refs:\n{string.Join("\n", iss)}");
        }
    }

    // ── 4. The guard keeps its teeth: undeclared roots are still flagged ─────────

    [Fact]
    public void UndeclaredRoots_StillFlagged_ByEveryShapeFamily()
    {
        var teethShapes = new HashSet<string>();
        for (var i = 0; i < 120; i++)
        {
            var rng = FuzzHarness.SeededRng(Seed + 3, i, 12289);
            var members = Enumerable.Range(0, rng.Next(3, 7)).Select(_ => Ident(rng)).Distinct().ToList();
            var component = ComponentSource(rng, members);

            // Undeclared roots: distinct from every member, not $-prefixed, not keywords.
            var undeclared = Words.Where(w => !members.Contains(w))
                .OrderBy(_ => rng.Next()).Take(rng.Next(2, 5)).ToList();

            var htmlParts = new List<string>();
            foreach (var u in undeclared)
            {
                var (shapeName, build) = Shapes[rng.Next(Shapes.Length)];
                teethShapes.Add(shapeName);
                htmlParts.Add(build(u, Prop(rng)));
            }
            var html = string.Join("\n", htmlParts);

            var sym = TemplateBindingValidator.ExtractTemplateSymbols(html);
            foreach (var u in undeclared)
                Assert.True(sym.Contains(u),
                    $"undeclared root '{u}' was not extracted as a symbol (shape family lost)");

            var issues = TemplateBindingValidator.ValidateTemplateBindings("a.html", html, component);
            foreach (var u in undeclared)
                Assert.True(issues.Any(issue => issue.Contains($"references '{u}'")),
                    $"undeclared root '{u}' was not flagged:\n{string.Join("\n", issues)}");
        }
        FuzzHarness.AssertExercised(teethShapes.Count, "teeth binding shapes");
    }
}
