# Give rclone its own Google sign-in (client ID)

**Move to Google Drive** and **Bring back** upload through rclone. Right now rclone signs in to Google
with a *shared* client ID that every rclone user borrows. Google is retiring it during 2026. This page
replaces it with your own. It's free, needs no billing, and takes about 15 minutes.

## When you need this

Any of these means it's time:

- A Move or Bring back job fails with `rclone copy failed, and nothing on this PC was deleted: …`
  and the reason mentions `invalid_client`, `unauthorized_client`, `401`, `403`, `rateLimitExceeded`
  or `quota`.
- `rclone lsd gdrive:` fails with one of those errors.

Nothing on your PC is lost when this happens. A failed upload never deletes local files, so the
video just stays where it is until rclone works again. You can also do this early; it doesn't break
anything.

**What you'll end up with:** a **Client ID** (long, ends in `.apps.googleusercontent.com`) and a
**Client secret** (starts with `GOCSPX-`). YtPipeline doesn't change at all: the remote is still called
`gdrive`, and Settings → Storage stays the same.

---

## Part 1: Google Cloud Console (in your browser)

Google renames these menus now and then. If a name doesn't match, rclone keeps current wording at
<https://rclone.org/drive/#making-your-own-client-id>.

1. **Sign in.** Open <https://console.cloud.google.com/> with the Google account whose Drive you use.
   On a first visit, accept the terms. Skip any offer to start a free trial or add billing; you
   don't need it.

2. **Create a project.** Click the project picker at the top, then **New project**. Name it `rclone`,
   click **Create**, and make sure the picker shows `rclone` before you go on.

3. **Turn on the Drive API.** Open
   <https://console.cloud.google.com/apis/library/drive.googleapis.com> and click **Enable**.

4. **Set up the consent screen.** Go to menu ☰ → **APIs & Services** → **OAuth consent screen**
   (newer consoles call this **Google Auth Platform**), then **Get started**.
   - App name: `rclone`. User support email: your email. Click **Next**.
   - Audience: **External**. Click **Next**.
   - Contact information: your email. Click **Next**.
   - Tick the agreement, then **Continue** → **Create**.

5. **Add the Drive scopes.** Open **Data Access** → **Add or remove scopes**. Scroll to **Manually add
   scopes**, paste this line, then click **Add to table** → **Update** → **Save**:

   ```
   https://www.googleapis.com/auth/docs,https://www.googleapis.com/auth/drive,https://www.googleapis.com/auth/drive.metadata.readonly
   ```

6. **Publish the app. Don't skip this one.** Open **Audience**. Under Publishing status (it says
   *Testing*), click **Publish app** and confirm. It should now say **In production**.
   While an app is in *Testing*, Google expires its sign-in **every 7 days**, and Move to Google Drive
   would break once a week. You don't need Google's verification: "In production, not verified" is
   fine for your own use.

7. **Create the client.** Open **Clients** → **Create client**. (Older consoles: **Credentials** →
   **Create credentials** → **OAuth client ID**.)
   - Application type: **Desktop app**. Name: `rclone`. Click **Create**.
   - A box shows the **Client ID** and **Client secret**. Click **Download JSON**, or copy both into a
     note, **right now**, because Google shows the secret only once. If you lose it, open the client
     and use **Add secret**.
   - Keep them private. Save them somewhere outside the YtPipeline folder, not in chats or
     screenshots.

---

## Part 2: Point rclone at your client (in a terminal)

First check that YtPipeline isn't moving anything to or from Drive. Then open **PowerShell from the
Start menu**, not a terminal inside the Claude app. A terminal started by Claude saves rclone's
settings into Claude's private copy of AppData, where YtPipeline can't see them. That is what broke
Move and Bring back on 2026-09-30.

rclone's settings live in `C:\Users\M4tth3w\.config\rclone\rclone.conf`. Running
`rclone config file` should print that path. If it prints an `AppData` path instead, stop and ask.

8. **Back up rclone's settings.**

   ```powershell
   Copy-Item "$env:USERPROFILE\.config\rclone\rclone.conf" "$env:USERPROFILE\.config\rclone\rclone.conf.before-client-id"
   ```

9. **Give rclone your ID and secret.** Replace the two placeholders and keep the quotes:

   ```powershell
   rclone config update gdrive client_id "PASTE_CLIENT_ID" client_secret "PASTE_CLIENT_SECRET"
   ```

   Your browser opens so you can sign in again. The old sign-in belongs to the shared client and
   won't work with yours.
   - Pick the **same Google account** as before.
   - *"Google hasn't verified this app"* is expected, because it's your own app. Click **Advanced** →
     **Go to rclone (unsafe)** → **Continue**.
   - Allow access. When the browser says **Success!**, switch back to the terminal. rclone finishes
     by itself.

   If the browser doesn't open, run `rclone config reconnect gdrive:` and open the link it prints.

10. **Check it worked.**

    ```powershell
    rclone lsd gdrive:YtPipeline
    ```

    You should see your channel folders (for example `barely-an-expert`), and the line *"This remote
    uses rclone's shared Google Drive client_id … being retired"* should be **gone**. If that line is
    gone, rclone is using your own client.

11. **Back in YtPipeline,** click Move to Google Drive (or Bring back) again on any video that failed.
    You don't need to change anything in Settings.

---

## If something goes wrong

| What you see | Why | Fix |
|---|---|---|
| `Error 403: access_denied`, or "has not completed the Google verification process" during sign-in | The app is still in *Testing* | Step 6. Or add your email under **Audience** → **Test users** |
| `invalid_client` | The ID or secret was pasted wrong (a missing piece or an extra space) | Redo step 9 |
| `Google Drive API has not been used in project … or it is disabled` | Step 3 was missed | Enable the API, wait 2 minutes, retry |
| Works, then fails again a week later | The app is still in *Testing* | Step 6 |
| `didn't find section in config file ("gdrive")` / "can't find its Google Drive sign-in" | rclone.conf isn't where YtPipeline's rclone looks | Run `rclone config file` in a Start-menu PowerShell; the file belongs in `%USERPROFILE%\.config\rclone\` |
| Want to undo | | `Copy-Item "$env:USERPROFILE\.config\rclone\rclone.conf.before-client-id" "$env:USERPROFILE\.config\rclone\rclone.conf" -Force` |

Undoing only helps before Google retires the shared client ID. After that, the old settings won't
work either.
