import { useState } from "react";
import { ChevronDown, ExternalLink } from "lucide-react";
import { Callout, CopyButton, Step, Steps } from "./ui";
import { cn } from "@/lib/utils";

/**
 * Step-by-step setup for each publishing target. Written as instructions the user follows
 * on the platform's own site — nothing here talks to an API. Kept beside the credential
 * fields because "where do I get a Page ID?" is the actual blocker, not the form.
 */
export function PublishingGuide({ platform }: { platform: "youtube" | "facebook" | "instagram" }) {
  const [open, setOpen] = useState(false);

  return (
    <div className="overflow-hidden rounded-lg border border-white/[0.07] bg-white/[0.015]">
      <button
        onClick={() => setOpen((v) => !v)}
        className="flex w-full items-center gap-2 px-3.5 py-2.5 text-left transition-colors hover:bg-white/[0.03]"
      >
        <span className="flex-1 text-xs font-semibold text-white/70">
          How to get these credentials
        </span>
        <ChevronDown className={cn("size-3.5 text-white/30 transition-transform", open && "rotate-180")} />
      </button>

      {open && (
        <div className="fade-in space-y-4 border-t border-white/[0.06] px-3.5 py-4">
          {platform === "youtube" && <YoutubeGuide />}
          {platform === "facebook" && <FacebookGuide />}
          {platform === "instagram" && <InstagramGuide />}
        </div>
      )}
    </div>
  );
}

function Link({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <a href={href} target="_blank" rel="noreferrer" className="inline-flex items-center gap-0.5">
      {children}<ExternalLink className="size-2.5" />
    </a>
  );
}

// ─── YouTube ─────────────────────────────────────────────────────────────────

function YoutubeGuide() {
  return (
    <>
      <Steps>
        <Step n={1} title="Create a Google Cloud project">
          Go to <Link href="https://console.cloud.google.com/">console.cloud.google.com</Link> and
          create a project (any name).
        </Step>
        <Step n={2} title="Enable the YouTube Data API v3">
          <em>APIs &amp; Services → Library</em> → search <code>YouTube Data API v3</code> → Enable.
        </Step>
        <Step n={3} title="Configure the OAuth consent screen">
          <em>APIs &amp; Services → OAuth consent screen</em>. Choose <strong>External</strong>, fill
          the app name and your email, then add <strong>your own Google account as a test user</strong>.
          <p>Add the scope <code>https://www.googleapis.com/auth/youtube.upload</code>.</p>
          <p>Staying in "Testing" is fine for your own channels — no Google verification needed.</p>
        </Step>
        <Step n={4} title="Create an OAuth client ID">
          <em>Credentials → Create credentials → OAuth client ID</em>. Application type:
          <strong> Desktop app</strong>. Copy the <strong>Client ID</strong> and
          <strong> Client secret</strong> into the fields above.
        </Step>
        <Step n={5} title="Set the target channel — per channel, not here">
          YouTube Studio → <em>Settings → Channel → Advanced settings</em> gives an id starting
          with <code>UC…</code>. Paste it under <strong>Channels → your channel → Publish
          destinations</strong>.
          <p>
            One OAuth client covers every channel on the account, but consent is granted to one
            channel at a time — so the destination belongs to the channel, not to the app.
          </p>
        </Step>
      </Steps>

      <Callout tone="warn" title="Upload quota is the real limit">
        A video insert costs about <strong>1,600 units</strong> against the default
        <strong> 10,000 units/day</strong> — roughly <strong>6 uploads per day</strong>. You can
        request more in the Cloud console, but plan around that number.
      </Callout>

      <Callout tone="info">
        First upload opens a browser consent window and caches a refresh token, so later runs are
        unattended.
      </Callout>
    </>
  );
}

// ─── Facebook ────────────────────────────────────────────────────────────────

function FacebookGuide() {
  return (
    <>
      <Steps>
        <Step n={1} title="Create a Meta app">
          <Link href="https://developers.facebook.com/apps/">developers.facebook.com/apps</Link> →
          <em> Create App</em> → use the <strong>Business</strong> type.
        </Step>
        <Step n={2} title="Copy the App ID and App Secret">
          <em>App settings → Basic</em>. Paste both above.
        </Step>
        <Step n={3} title="Grant the Page permissions">
          Open the <Link href="https://developers.facebook.com/tools/explorer/">Graph API Explorer</Link>,
          pick your app, then request these permissions:
          <p>
            <code>pages_show_list</code> <code>pages_read_engagement</code> <code>pages_manage_posts</code>
          </p>
        </Step>
        <Step n={4} title="Get your Page ID and Page token">
          Still in the Explorer, call:
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1">GET /me/accounts</code>
            <CopyButton text="/me/accounts" />
          </div>
          <p>
            The response lists each Page you manage with its <code>id</code> (the
            <strong> Page ID</strong>) and an <code>access_token</code> (the
            <strong> Page access token</strong>).
          </p>
          <p>
            Both go under <strong>Channels → your channel → Publish destinations</strong> — one
            Page per channel, each with its own token.
          </p>
        </Step>
        <Step n={5} title="Make the token long-lived">
          Short-lived tokens expire in about an hour. Exchange your <em>user</em> token first:
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1 break-all">
              GET /oauth/access_token?grant_type=fb_exchange_token&amp;client_id=APP_ID&amp;client_secret=APP_SECRET&amp;fb_exchange_token=SHORT_TOKEN
            </code>
            <CopyButton text="/oauth/access_token?grant_type=fb_exchange_token&client_id=APP_ID&client_secret=APP_SECRET&fb_exchange_token=SHORT_TOKEN" />
          </div>
          <p>
            Then call <code>/me/accounts</code> again <em>with the long-lived user token</em> — the
            Page tokens it returns don't expire.
          </p>
        </Step>
      </Steps>

      <Callout tone="info" title="Publishing endpoint">
        Videos post to <code>/{"{page-id}"}/videos</code> using the Page token. Facebook accepts a
        direct file upload, so a locally rendered mp4 works without hosting it anywhere.
      </Callout>
    </>
  );
}

// ─── Instagram ───────────────────────────────────────────────────────────────

function InstagramGuide() {
  return (
    <>
      <Callout tone="warn" title="Account prerequisites">
        Instagram publishing only works from a <strong>Professional</strong> account (Business or
        Creator) that is <strong>linked to a Facebook Page</strong>. Personal accounts cannot
        publish through the API at all.
      </Callout>

      <Steps>
        <Step n={1} title="Convert the Instagram account">
          In the Instagram app: <em>Settings → Account type → Switch to professional account</em>,
          then link it to your Facebook Page.
        </Step>
        <Step n={2} title="Reuse your Meta app">
          The same app as Facebook. Add the <strong>Instagram</strong> product to it, and paste the
          App ID / App Secret above.
        </Step>
        <Step n={3} title="Request the publishing permissions">
          <p><code>instagram_business_basic</code> <code>instagram_business_content_publish</code></p>
          <p>
            These replaced the older <code>instagram_basic</code> / <code>instagram_content_publish</code>
            scopes, which were removed in January 2025.
          </p>
        </Step>
        <Step n={4} title="Find your IG user ID">
          It is not your @handle. Call:
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1 break-all">GET /{"{page-id}"}?fields=instagram_business_account</code>
            <CopyButton text="?fields=instagram_business_account" />
          </div>
          <p>
            The returned <code>id</code> is the <strong>IG user ID</strong>. It and its access
            token go under <strong>Channels → your channel → Publish destinations</strong>.
          </p>
        </Step>
        <Step n={5} title="Host the render somewhere public">
          Publishing is two calls — <code>POST /{"{ig-user-id}"}/media</code> with a
          <code> video_url</code>, then <code>POST /{"{ig-user-id}"}/media_publish</code>. Set the
          channel's <strong>public video base URL</strong> to wherever its mp4s will be served
          from.
        </Step>
      </Steps>

      <Callout tone="err" title="Instagram cannot read local files">
        Meta's servers <strong>fetch the video from your URL</strong> — there is no direct upload on
        this flow. A video sitting on your D: drive is unreachable, so Instagram publishing needs
        the mp4 hosted somewhere public first (S3, R2, a CDN, any static host). That's what each
        channel's <strong>public video base URL</strong> is for.
      </Callout>

      <Callout tone="warn">
        Instagram allows <strong>100 API-published posts per rolling 24 hours</strong>, counting all
        content types. Publishing to accounts you don't own also requires Meta <em>App Review</em>.
      </Callout>
    </>
  );
}
