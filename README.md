# Instagram Story Viewer (C#)

A .NET 8 console program for viewing Instagram stories with **your own account's session**. It talks to the same endpoints the instagram.com web app uses.

## Features
- List the stories of the accounts you follow (●: not seen yet)
- Show the stories of any public account, or a private account you follow
- Open a story in your browser, or download stories in their highest quality (`.jpg` / `.mp4`)
- Mark stories as seen (optional; your name then appears in the story owner's viewers list)
- Handles Instagram's responses: expired session, account verification (checkpoint), rate limits (429), private accounts, and non-JSON responses
- Automatic retries with exponential backoff, plus a delay between requests to avoid getting blocked

## Download (Windows)
A ready-to-run executable is at [`release/win-x64/InstagramStoryViewer.exe`](release/win-x64/InstagramStoryViewer.exe). It is a single self-contained file, so .NET does **not** need to be installed. Double-click it, or run it from a terminal:
```powershell
.\InstagramStoryViewer.exe --user natgeo --download
```

To rebuild the exe yourself:
```bash
cd src/InstagramStoryViewer
dotnet publish -c Release -r win-x64 -o ../../release/win-x64
```

## Requirements (to build from source)
- [.NET 8 SDK](https://dotnet.microsoft.com/download)

## Getting your session (sessionid)
1. Open https://www.instagram.com in your browser and log in.
2. Press `F12` and go to **Application** (or **Storage** in Firefox), then **Cookies**, then `https://www.instagram.com`.
3. Copy the value of `sessionid`, and preferably `csrftoken` too (marking stories as seen needs it).

Give the program the session in one of these ways:
- Paste it when the program asks for it (input is hidden, and you can choose to save it to `session.txt`)
- Use the environment variable `IG_COOKIE`:
  ```bash
  # Windows PowerShell
  $env:IG_COOKIE = "sessionid=XXXX; csrftoken=YYYY"
  # Linux / macOS
  export IG_COOKIE="sessionid=XXXX; csrftoken=YYYY"
  ```

> ⚠️ `sessionid` gives full access to your account. Never share it, and don't upload `session.txt` anywhere (it's in `.gitignore`).

## Running
```bash
cd src/InstagramStoryViewer

dotnet run                                   # interactive mode (menu)
dotnet run -- --tray                         # stories of the accounts you follow
dotnet run -- --user natgeo                  # show a user's stories
dotnet run -- --user natgeo --download       # download them to the stories/natgeo folder
dotnet run -- --user natgeo --seen           # mark them as seen
dotnet run -- --user natgeo -d --out D:\IG   # choose the download folder
```

## Project structure
```
src/InstagramStoryViewer/
├── Program.cs                    # interface, menu, and command-line options
├── Api/
│   ├── InstagramClient.cs        # HTTP requests, response analysis, error handling, retries
│   ├── InstagramApiException.cs  # error types (session / verification / rate limit ...)
│   └── SessionCookies.cs         # cookie parsing (sessionid / csrftoken / ds_user_id)
├── Models/ApiModels.cs           # JSON response models
├── Json/FlexibleStringConverter.cs  # reads ids whether they come back as numbers or strings
└── Services/StoryDownloader.cs   # story downloads
```

## Endpoints used
| Purpose | Endpoint |
|---|---|
| Username to id | `GET /api/v1/users/web_profile_info/?username=` |
| Stories of followed accounts | `GET /api/v1/feed/reels_tray/` |
| A user's stories | `GET /api/v1/feed/reels_media/?reel_ids=` |
| Mark as seen | `POST /api/v1/stories/reel/seen` |

## Important notes
- These are unofficial Instagram endpoints, and they may change without notice. If something stops working, the first thing to check is the response the program prints.
- Using automated tools may violate Instagram's terms of use. Use the program reasonably and only for personal use. Too many requests can get your account temporarily restricted.
- Respect people's privacy. Don't republish other people's stories without their permission.
