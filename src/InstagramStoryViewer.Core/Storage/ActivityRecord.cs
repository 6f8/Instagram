namespace InstagramStoryViewer.Storage;

/// <summary>What the app did for the user.</summary>
public enum WatchMode { Manual, Preview, Auto, AutoLike, Comment }

/// <summary>Result of one attempt, as recorded in the history.</summary>
public enum WatchOutcome
{
    NotShown,   // the story/post could not be opened (expired, deleted, browser not logged in, stopped)
    Shown,      // shown, but Instagram's response did not say whether it counted
    Verified,   // confirmed (seen marker for stories, button state for likes, posted text for comments)
    Failed      // shown, but the action did not take effect
}

/// <param name="Url">Link to the item (posts); null means the story URL built from username + media id.</param>
/// <param name="IsPost">A post rather than a story.</param>
/// <param name="Text">Comment text (comments recorded by versions that could still post comments).</param>
public sealed record WatchRecord(DateTime Time, string Username, string MediaPk, WatchMode Mode, WatchOutcome Outcome,
    string? Url = null, bool IsPost = false, string? Text = null);
