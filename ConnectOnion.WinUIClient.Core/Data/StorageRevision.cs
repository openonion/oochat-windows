namespace ConnectOnion.WinUIClient.Data;

/// <summary>
/// A process-wide counter bumped by every write that could change what the sidebar shows:
/// the agent list and its selection, the conversation index and its active pointer, and the
/// messages the row previews are drawn from.
///
/// <para>It exists so a caller can answer "has anything changed since I last read?" without
/// performing the read. <c>ShellSidebar.RefreshAsync</c> runs on every navigation, every session
/// change and every rename, and its render-signature guard could only short-circuit the UI
/// rebuild <i>after</i> it had already loaded the whole agent table, the whole session table and a
/// batch of message previews — so the guard saved the layout pass and none of the I/O.</para>
///
/// <para>Deliberately one counter rather than one per table. It is an invalidation epoch, and the
/// asymmetry matters: a spurious bump costs one redundant refresh, while a missed bump leaves
/// stale rows on screen. Coarse is the safe direction, and nothing here is fine-grained enough to
/// be worth the risk of getting a narrower rule wrong.</para>
///
/// <para>In-process only. It is not persisted and means nothing across a restart, which is
/// correct — a fresh process has read nothing yet and starts from a value no cached snapshot
/// can match.</para>
/// </summary>
public static class StorageRevision
{
    private static long _current;

    /// <summary>The current epoch. Compare a stored value against this to decide whether a
    /// cached read is still good. Never zero after the first write, so <c>0</c> is usable as
    /// "nothing observed yet".</summary>
    public static long Current => System.Threading.Interlocked.Read(ref _current);

    /// <summary>Records that persisted state a reader may have cached has changed.</summary>
    public static void Bump() => System.Threading.Interlocked.Increment(ref _current);
}
