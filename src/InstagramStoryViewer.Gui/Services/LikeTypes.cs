namespace InstagramStoryViewer.Gui.Services;

/// <summary>A story or post to like in the embedded browser.</summary>
public sealed record LikeRequest(string Url, bool IsStory);

public enum LikeOutcome
{
    NotShown,     // page could not be opened (expired, deleted, not logged in, stopped)
    NoButton,     // page opened but no like button was found
    AlreadyLiked, // it was liked before
    Liked,        // clicked, and the button now reads "Unlike"
    Failed        // clicked, but the button did not change
}

public enum CommentOutcome
{
    NotShown,       // the post could not be opened (deleted, private, stopped)
    LoginRequired,  // the embedded browser is not logged in
    CommentsOff,    // the owner turned comments off or limited them
    Rejected,       // Instagram showed an error ("Couldn't post comment")
    Unconfirmed,    // sent, but the comment did not show up on the page in time
    Posted          // the comment appears under the post
}
