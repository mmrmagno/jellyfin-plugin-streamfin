using StreamFin;

// The checks' tiny harness, shared by every checks file. A failed check throws, so the run stops at the first failure.
static class Harness
{
    public static int Count { get; private set; }
    public static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); Count++; }
    public static void Reject(Action action, string name) { try { action(); } catch (ProviderException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
    public static Exception? Throws(Action action) { try { action(); } catch (Exception ex) { return ex; } return null; }
    public static void Contained(Action action, string name) { try { action(); } catch (ProviderException) { } Check(true, name); }
    public static async Task RejectAsync(Func<Task> action, string name) { try { await action(); } catch (ProviderException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
}
