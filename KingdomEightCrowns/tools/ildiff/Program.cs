// ildiff OLD.dll NEW.dll [--verbose]
// Compares two builds of a plugin at the level that matters for runtime binding:
//  * external assembly references and every external member/type referenced (by full signature, scope-qualified)
//  * per-method multiset of external references, string literals and numeric constants
// Compiler-generated closure/lambda names are normalized so decompile/recompile noise is ignored.
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

var verbose = args.Contains("--verbose");
var a = Load(args[0]); var b = Load(args[1]);
int diffs = 0;

Console.WriteLine("== Assembly references");
var ra = a.AssemblyReferences.Select(r => r.Name).ToHashSet(); var rb = b.AssemblyReferences.Select(r => r.Name).ToHashSet();
foreach (var x in ra.Except(rb)) { Console.WriteLine($"  - {x}"); diffs++; }
foreach (var x in rb.Except(ra)) { Console.WriteLine($"  + {x}"); diffs++; }

Console.WriteLine("== External member references (non-BCL)");
var ma = ExtMembers(a); var mb = ExtMembers(b);
foreach (var x in ma.Except(mb).OrderBy(x => x)) { Console.WriteLine($"  - {x}"); diffs++; }
foreach (var x in mb.Except(ma).OrderBy(x => x)) { Console.WriteLine($"  + {x}"); diffs++; }

Console.WriteLine("== Per-method operand multisets");
var pa = PerMethod(a); var pb = PerMethod(b);
foreach (var k in pa.Keys.Union(pb.Keys).OrderBy(k => k)) {
  pa.TryGetValue(k, out var la); pb.TryGetValue(k, out var lb);
  la ??= new(); lb ??= new();
  var minus = MultisetExcept(la, lb); var plus = MultisetExcept(lb, la);
  if (minus.Count == 0 && plus.Count == 0) continue;
  diffs++;
  Console.WriteLine($"  {k}: -{minus.Count} +{plus.Count}");
  if (verbose) { foreach (var x in minus.Take(12)) Console.WriteLine($"      - {x}"); foreach (var x in plus.Take(12)) Console.WriteLine($"      + {x}"); }
}
Console.WriteLine(diffs == 0 ? "RESULT: equivalent" : $"RESULT: {diffs} difference group(s)");
return;

static ModuleDefinition Load(string p) {
  var res = new DefaultAssemblyResolver(); res.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(p)));
  return ModuleDefinition.ReadModule(p, new ReaderParameters { AssemblyResolver = res });
}
static bool IsBcl(string scope) => scope is "netstandard" or "mscorlib" or "System.Runtime" or "System.Private.CoreLib" || scope.StartsWith("System.");
static string Norm(string s) {
  s = Regex.Replace(s, @"<>c__DisplayClass\d+_\d+", "<>c__DisplayClassN");
  s = Regex.Replace(s, @"<(\w+)>b__\d+_\d+", "<$1>b__N");
  s = Regex.Replace(s, @"<(\w+)>g__(\w+)\|\d+_\d+", "<$1>g__$2|N");
  s = Regex.Replace(s, @"<>9__\d+_\d+", "<>9__N");
  s = Regex.Replace(s, @"CS\$<>8__locals\d+", "CS$<>8__localsN");
  s = Regex.Replace(s, @"<(\w+)>d__\d+", "<$1>d__N");
  s = Regex.Replace(s, @"<>u__\d+", "<>u__N");
  return s;
}
static string Scope(TypeReference t) { t = t.GetElementType(); while (t.DeclaringType != null) t = t.DeclaringType; return t.Scope?.Name ?? "?"; }
static HashSet<string> ExtMembers(ModuleDefinition m) {
  var set = new HashSet<string>();
  foreach (var r in m.GetMemberReferences()) { var sc = Scope(r.DeclaringType); if (!IsBcl(sc)) set.Add($"[{sc}] {r.FullName}"); }
  foreach (var t in m.GetTypeReferences()) { var sc = Scope(t); if (!IsBcl(sc)) set.Add($"[{sc}] type {t.FullName}"); }
  return set;
}
static Dictionary<string, List<string>> PerMethod(ModuleDefinition m) {
  var d = new Dictionary<string, List<string>>();
  foreach (var t in AllTypes(m.Types)) foreach (var md in t.Methods) {
    if (!md.HasBody) continue;
    var key = Norm(md.FullName);
    var list = d.TryGetValue(key, out var l) ? l : (d[key] = new List<string>());
    foreach (var ins in md.Body.Instructions) {
      switch (ins.Operand) {
        case MemberReference mr when mr is MethodReference || mr is FieldReference || mr is TypeReference:
          var sc = mr is TypeReference tr ? Scope(tr) : Scope(mr.DeclaringType);
          list.Add(Norm($"{ins.OpCode.Code.ToString().Split('_')[0]} [{(IsBcl(sc) ? "bcl" : sc)}] {mr.FullName}")); break;
        case string s: list.Add($"ldstr \"{s}\""); break;
        case int i when ins.OpCode.Code != Code.Ldc_I4_S && ins.OpCode.Code != Code.Ldc_I4: break;
        case sbyte sb: list.Add($"ldc {sb}"); break;
        case int i2: list.Add($"ldc {i2}"); break;
        case long l2: list.Add($"ldc.i8 {l2}"); break;
        case float f: list.Add($"ldc.r4 {f}"); break;
        case double db: list.Add($"ldc.r8 {db}"); break;
      }
      if (ins.OpCode.Code >= Code.Ldc_I4_M1 && ins.OpCode.Code <= Code.Ldc_I4_8) list.Add($"ldc {(int)ins.OpCode.Code - (int)Code.Ldc_I4_0}");
    }
  }
  return d;
}
static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> ts) { foreach (var t in ts) { yield return t; foreach (var n in AllTypes(t.NestedTypes)) yield return n; } }
static List<string> MultisetExcept(List<string> x, List<string> y) {
  var counts = y.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count()); var res = new List<string>();
  foreach (var s in x) { if (counts.TryGetValue(s, out var c) && c > 0) counts[s] = c - 1; else res.Add(s); }
  return res;
}
