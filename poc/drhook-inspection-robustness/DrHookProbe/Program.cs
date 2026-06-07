// DrHook object-inspection isolation probe (ADR-007).
// The depth-2 locals inspection of QueryResults.FromMaterializedWithGraphContext crashed
// the DrHook server. Was it the inspection DEPTH, or a specific TYPE in the frame
// (ReadOnlySpan ref-struct? List<struct>?)? Four frames, ascending suspicion. Break at the
// marker line in each and inspect at depth 2; the first to crash isolates the culprit.

Console.WriteLine("drhook-isolation start");
StructCase();
ListIntCase();
ListStructCase();
SpanCase("abcdef");
Console.WriteLine("drhook-isolation done");

// Safest first: a plain struct (int + string).
static void StructCase()
{
    var p = new Pair(7, "seven");
    Console.WriteLine($"[STRUCT] ready A={p.A} B={p.B}");   // <-- BP: depth-2 inspect 'p'
}

// A collection of primitives.
static void ListIntCase()
{
    var list = new List<int> { 1, 2, 3 };
    Console.WriteLine($"[LISTINT] ready count={list.Count}"); // <-- BP: depth-2 inspect 'list'
}

// A collection of structs-with-a-string (shape of List<MaterializedRow>).
static void ListStructCase()
{
    var list = new List<Pair> { new(1, "a"), new(2, "b") };
    Console.WriteLine($"[LISTSTRUCT] ready count={list.Count}"); // <-- BP: depth-2 inspect 'list'
}

// Prime suspect: a ref struct (ReadOnlySpan), as in the Mercury frame's 'source' arg.
static void SpanCase(string s)
{
    ReadOnlySpan<char> span = s.AsSpan();
    Console.WriteLine($"[SPAN] ready len={span.Length}");   // <-- BP: depth-2 inspect 'span'
}

struct Pair
{
    public int A;
    public string B;
    public Pair(int a, string b) { A = a; B = b; }
}
