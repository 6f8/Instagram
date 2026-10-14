# IG Story Viewer Pro

A Windows desktop app (WPF, .NET 8) for viewing, downloading and watching Instagram stories with **your own account**.
The interface is in Arabic (right to left) with a dark "neon dashboard" design.

![Dashboard](docs/screenshots/dashboard.png)

## Download
**[⬇ InstagramStoryViewer.exe (latest version)](https://github.com/6f8/Instagram/releases/latest/download/InstagramStoryViewer.exe)**. Every version is on the [Releases](https://github.com/6f8/Instagram/releases) page.

- It's a single self-contained file, so .NET doesn't need to be installed (Windows 10/11, 64-bit). Double-click to run it.
- Next to it is `InstagramStoryViewer.exe.sha256`, which you can use to verify the file (`Get-FileHash InstagramStoryViewer.exe` in PowerShell).
- The file isn't digitally signed, so Windows SmartScreen may warn you the first time: click **More info**, then **Run anyway**.
- The app checks for new versions on every start and every 6 hours. When one is available, press **Update now** (on the dashboard or in Settings):
  - It downloads the new exe and checks it against the published SHA-256.
  - It then replaces itself and restarts.
  - If it can't (for example, the exe is in a protected folder), it opens the download page instead.

## Features

### Viewing and downloading
- **Dashboard:**
  - Stats at the top.
  - A table of the selected account's stories.
  - A preview panel that plays images and videos.
  - The list of accounts you follow.
- **Full-screen viewer:** double-click a story, or use the button in the preview panel.
  - Moves to the next story automatically.
  - Keys: `←` next, `→` previous, `Space` pause, `D` download, `Esc` close.
- **Highlights:** browse a profile's pinned stories and download them.
- **Downloads:** highest available quality.
  - Each file is saved to `<download folder>/<username>/`, and highlights to `<username>_highlights`.
  - Never downloaded twice, never saved incomplete.
  - Expired links are refreshed automatically.
- **Search** for any public account, or a private account you follow.

### Following and favorites
- Search by name, and filter: All, New, or Favorites.
- **Favorites ⭐:**
  - Starred accounts appear first.
  - Their new stories can be downloaded automatically.
  - You get a Windows notification when they post something new.

### Watching (marking as seen)
- **How it works:** the story actually plays in a small embedded-browser window in the corner of the screen, so Instagram records a real view.
- **Verification:** after watching, the app asks Instagram whether the view was recorded (**confirmed / unconfirmed / failed**).
- **"Auto-watch" box on the dashboard:**
  - Turn it on and the app watches every new story from the accounts you follow, with no interaction from you, then keeps up with anything new after each refresh.
  - A progress bar shows "watched X of Y · Z remaining" and has a Stop button.
- **Auto-watch (optional):**
  - When you preview a story.
  - Or your followed accounts' new stories, in the background.
  - Within a daily limit you type yourself (any number) and outside **quiet hours** (default 01:00–08:00).
- **Exclusion list 👁‍🗨:** accounts the app never watches automatically. It asks before watching them manually.
- **Watch history:** everything the app watched for you, when, and the result.

### Auto-like (optional, off by default)
- **What it likes:**
  - New stories, either from your favorites or from everyone you follow.
  - New posts (last 3 days) from your favorites only.
- **How it works:**
  - Runs after every refresh, even when the app is minimized to the notification area.
  - Presses the real like button in the small corner window, then verifies the button switched to "Unlike".
  - It never clicks a comment's like button by mistake.
- **Limits:** a daily cap you type yourself in Settings (any number), at most 10 per cycle, quiet hours, the exclusion list, and the hourly request cap.
- **History:** every like is recorded in the history.
- Overusing automated likes can get your account restricted.

### Commenting on a post
- **Comment** page (`Ctrl+5`):
  1. Paste the link of a post or reel.
  2. Write your comment.
  3. Press **Post comment**.
- The app first checks the comment against Instagram's limits:
  - At most 2,200 characters.
  - At most 30 hashtags.
  - At most 5 mentions.
- Before posting, it shows you the comment and the post owner and asks you to confirm. It warns you if you already posted the same comment on that post.
- **How it works:**
  - The post opens in the embedded browser.
  - The comment is typed into the post's own comment box and **Post** is pressed.
  - The app then waits until the comment actually appears on the page: **posted**, **sent but unconfirmed**, or **not posted** (comments off, or an error from Instagram).
- Every comment is recorded in the history together with its text.

### Not following back
- The **Not following back** page (`Ctrl+6`) lists the accounts you follow that don't follow you.
- **How it works:** press **Check now**. The app reads your *following* and *followers* lists and compares them.
- **Where the lists are read:** from inside the logged-in instagram.com page in the embedded browser, with the same requests the site makes when you scroll the followers dialog. Instagram answers the same requests sent from outside the browser with "please wait a few minutes".
- **Request cost:**
  - Every 12 accounts cost one request (the page size the site uses), with a pause of 1.5–3.5 s between requests.
  - The app tells you the cost first, and asks to confirm if it is more than 10 requests.
  - It refuses to start if the hourly request cap would cut the check short, because a partial followers list would give wrong results.
- It warns you if Instagram returned fewer followers than your profile shows.
- **On the list:**
  - Search by name.
  - Hide verified accounts.
  - Copy the shown usernames.
  - Open any profile.
- The last result is kept (`%AppData%\InstagramStoryViewer\not-following-back.json`) and shown again without new requests. It is deleted when you log out.

### Keyboard
- `Ctrl+1` … `Ctrl+7`: switch pages.
- `F5`: refresh.
- `Ctrl+F`: search.
- `Tab`: moves between controls with a visible focus ring. Icon buttons have names for screen readers.

![Following](docs/screenshots/following.png)

| History | Full screen |
|---|---|
| ![](docs/screenshots/history.png) | ![](docs/screenshots/fullscreen.png) |
| **Settings** | **Login** |
| ![](docs/screenshots/settings.png) | ![](docs/screenshots/login.png) |

### Protecting your account
- A cap on Instagram requests per hour that you set yourself (any number), with random spacing between requests.
- Refreshes happen at irregular intervals and stop when the window is closed or minimized.
- Cookies that Instagram rotates are saved automatically, so the session doesn't expire on its own.
- A session that Instagram ends is detected immediately, and requests stop.
- The session is stored encrypted with Windows DPAPI.
- The diagnostics file and the log mask tokens and account identifiers.

### Reliability
- **Requests to Instagram:**
  - Retried with exponential backoff and jitter. `Retry-After` is honored.
  - If Instagram itself says "too many requests", **all** requests pause for 10 minutes, to protect the account.
- **Background jobs:** auto-watch, auto-like and favorites run in one queue, one at a time. A refresh never starts an overlapping round.
- **Storage:**
  - History and the list of already-handled posts live in a small SQLite database. Old rows expire on their own: 180 days for history, 30 days for handled posts.
  - Settings are written once changes settle, not on every click. Older settings files are upgraded automatically.
- **Page selectors:** the button labels and the comment box the embedded browser looks for are kept in `config/selectors.json`.
  - When Instagram changes its page, fixing that file in the repository updates every copy of the app within a day, with no new release.
  - Only data is downloaded, never code.
  - A local override can be placed in `%AppData%\InstagramStoryViewer\selectors.json`.
- **Images:**
  - Profile pictures and thumbnails are cached on disk at the size they are shown (7 days, at most 150 MB).
  - Long lists only create the rows that are visible.
- **Crashes:** anything unexpected writes a crash report (sensitive values masked) next to the run log, and the app keeps running when it can.
- **Display:** per-monitor DPI, and the window reopens where you left it.

### Running in the background
- Closing the window keeps the app running in the **notification area** next to the clock (Open / Refresh / Auto-watch / Quit).
- An option to **start with Windows**, minimized.
- Only one copy of the app runs at a time.

## Logging in
1. **"Log in via Instagram"** opens instagram.com inside the app (Edge WebView2). Log in as usual, and the window closes on its own. Your password never passes through the app.
   WebView2 comes preinstalled on Windows 10/11. If it's missing, install the *Evergreen Runtime* from Microsoft.
2. **Paste the cookie manually:** in your browser, open `F12`, then **Application**, then **Cookies**, then `instagram.com`, and copy `sessionid` (preferably with `csrftoken`).

## File locations
| What | Where |
|---|---|
| Settings and encrypted session | `%AppData%\InstagramStoryViewer\settings.json` (+ `.bak`) |
| History (watched, liked, commented) | `%AppData%\InstagramStoryViewer\history.db` (SQLite; the old `watch-history.jsonl` is imported once) |
| Page selectors fetched from GitHub / local override | `%AppData%\InstagramStoryViewer\selectors.remote.json` / `selectors.json` |
| Last unexpected server response (tokens masked) | `%AppData%\InstagramStoryViewer\last-response.txt` |
| Daily run log (7 days) and crash reports | `%LocalAppData%\InstagramStoryViewer\logs\` |
| Image cache | `%LocalAppData%\InstagramStoryViewer\cache\images\` |
| Embedded browser data | `%LocalAppData%\InstagramStoryViewer\WebView2\` |

## Project structure
```
InstagramStoryViewer.sln
src/
├── InstagramStoryViewer.Core/     # no UI; fully unit-tested
│   ├── Api/                       # Instagram client (Polly retries, rate-limit cool-down), cookies, errors
│   ├── Json/                      # source-generated JSON (de)serializers
│   ├── Automation/                # Planner (what to watch/like), DailyCount, QuietHours, JobQueue
│   ├── Social/                    # post links, comment rules
│   ├── Storage/                   # SQLite history + handled media
│   └── Diagnostics/               # daily log, crash reports, sanitizer
├── InstagramStoryViewer.Gui/      # WPF interface (MVVM, dependency injection) → InstagramStoryViewer.exe
│   ├── Views/                     # main window, Pages/, full-screen viewer, embedded-browser window, login
│   ├── ViewModels/                # MainViewModel (partials) + AutoLike, Comment and Updates view models
│   ├── Services/                  # settings (debounced), history, image cache, page selectors, updater, tray
│   ├── Resources/                 # Strings.resx (view-model texts), built-in selectors.json
│   ├── Themes/Theme.xaml          # colors, styles and shared templates
│   └── Fonts/                     # Cairo + Material Icons (embedded)
└── InstagramStoryViewer/          # command-line version
config/selectors.json              # page selectors served to installed copies
tests/InstagramStoryViewer.Tests/  # xUnit tests + Fixtures/ (sample Instagram responses)
.github/workflows/build.yml        # CI: tests + coverage, exe build, optional signing, Releases
.github/dependabot.yml             # weekly dependency updates
designs/                           # the five proposed interface designs
```

## Building from source
```bash
dotnet build InstagramStoryViewer.sln
dotnet test tests/InstagramStoryViewer.Tests
dotnet publish src/InstagramStoryViewer.Gui -c Release -r win-x64 -o out
```
To publish a new version, bump `<Version>` in `InstagramStoryViewer.Gui.csproj`, then use either of these:
- Push a tag such as `v2.3.0`.
- Or, from the **Actions** page, open **build** and click **Run workflow** with **release** ticked. GitHub creates the tag itself.

In both cases, GitHub Actions builds the exe and adds it to Releases.

- Warnings fail the build (`Directory.Build.props`, `.editorconfig`).
- The core library must use `ConfigureAwait(false)` (CA2007).
- **Code signing:** add the repository secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT` and `AZURE_CERT_PROFILE` (Azure Trusted Signing), and every release is signed automatically. Without them it stays unsigned.

## Instagram endpoints used
| Purpose | Endpoint |
|---|---|
| Username to id | `GET /api/v1/users/web_profile_info/?username=` |
| Logged-in account info | `GET /api/v1/users/{id}/info/` |
| Stories of followed accounts | `GET /api/v1/feed/reels_tray/` |
| Stories of an account or highlight, or several accounts at once | `GET /api/v1/feed/reels_media/?reel_ids=..` |
| An account's latest posts (auto-like) | `GET /api/v1/feed/user/{id}/?count=..` |
| Following / followers (not following back) | `GET /api/v1/friendships/{id}/following/` and `/followers/?count=12&max_id=..`, fetched inside the embedded browser |
| Like | Presses the like button in the embedded browser |
| Post info for a comment (owner, comments off) | `GET /api/v1/media/{id}/info/` |
| Comment | Typed into the post's comment box in the embedded browser |
| An account's highlights | `GET /api/v1/highlights/{id}/highlights_tray/` |
| Mark as seen | Plays the story in the embedded browser (the old REST endpoint returns 404) |

If `www.instagram.com` returns an HTML page instead of data, the app automatically tries `i.instagram.com`.

## Important notes
- These are unofficial Instagram endpoints, and they may change without notice.
- Using automated tools may violate Instagram's terms of use. Use the app reasonably and only for personal use.
- Respect people's privacy, and don't republish other people's stories without their permission.
- Fonts: Cairo (OFL 1.1) and Material Icons (Apache 2.0). See `src/InstagramStoryViewer.Gui/Fonts/LICENSES.md`.
