# Email setup (M2 — verification codes)

The website sends a 6-digit code on sign-up ("confirm your email") and on **every** login ("finish
signing in"). Delivery goes through [Resend](https://resend.com). Until a Resend key is configured
the app runs in **log mode**: it writes each code to the container log instead of emailing it, so
verification still works for you as the operator.

## It already works without any setup (log mode)

With `TYPEBEAT_RESEND_API_KEY` unset, the active sender is `LogEmailSender`. To read a code:

```
docker logs typebeat-web-app-1 | grep '\[email:log\]'
```

Each entry shows the recipient, subject, and the code in the body. The startup log line
`Email sender: LogEmailSender` (or `ResendEmailSender`) confirms which mode is live.

## Enable real sending

### 1. Create a Resend account and verify the domain

1. Sign up at https://resend.com and open **Domains → Add Domain**. Enter `mingda.sh`.
2. Resend shows a set of DNS records to add. On **Cloudflare** (the DNS host for `mingda.sh`),
   go to the `mingda.sh` zone → **DNS → Records** and add exactly what Resend lists — typically:
   - an **MX** record for the bounce subdomain (e.g. `send`), value `feedback-smtp.<region>.amazonses.com`, priority `10`;
   - a **TXT** SPF record on that same subdomain (e.g. `send`), value `v=spf1 include:amazonses.com ~all`;
   - a **TXT** DKIM record `resend._domainkey`, value = the long `p=…` key Resend gives you;
   - (optional) a **TXT** DMARC record `_dmarc`, value `v=DMARC1; p=none;`.
   Copy the values from the Resend page rather than this list — the exact host/region differ per
   account. **Important on Cloudflare: set these records to "DNS only" (grey cloud), not proxied.**
3. Back in Resend, click **Verify**. Propagation is usually minutes; it can take up to a few hours.

### 2. Create an API key

Resend → **API Keys → Create API Key** (Sending access is enough). Copy the `re_…` value — it is
shown only once.

### 3. Configure the box

Add to `deploy/.env` on the server (git-ignored; sits next to `compose.prod.yml`):

```
TYPEBEAT_RESEND_API_KEY=re_your_key_here
TYPEBEAT_EMAIL_FROM=type!beat <noreply@mingda.sh>
```

`TYPEBEAT_EMAIL_FROM` must be an address on the verified domain. If you omit it, the app defaults to
`type!beat <noreply@mingda.sh>` anyway — set it explicitly if you want a different from-address.

### 4. Redeploy

```
cd /opt/typebeat        # wherever compose.prod.yml lives on the box
docker compose -f compose.prod.yml up -d
```

Check `docker logs typebeat-web-app-1` for `Email sender: ResendEmailSender`. Register a throwaway
account to confirm a real email arrives. If a send fails, the app logs the Resend response body
(`[email:resend] send failed …`) and shows the user a generic "couldn't send email, try again" — the
upstream error is never surfaced to the browser.

## New persistent state

Data Protection keys (which encrypt the mid-flow **challenge cookie** and sign antiforgery tokens)
are now persisted to `${TYPEBEAT_FILE_ROOT}/dpkeys` — i.e. `/data/dpkeys`, on the existing `appdata`
volume. This survives container redeploys so in-flight verifications and open forms are not
invalidated on every deploy. It is created automatically; `deploy/backup.sh` already tars `/data`.

## Notes

- The game client's OAuth login (`POST /oauth/token`) is **untouched** — it cannot do interactive
  codes. In-game registrants still get a verify code emailed; they enter it on the website. Uploads
  remain gated on `users.verified_at`, and `deploy/verify-user.sh <username>` still works as a manual
  override.
- Brute-force bounds (per code): 6 digits, one active code per (user, purpose), burned after 5 wrong
  guesses, 15 min expiry for verify / 10 min for login, 60 s resend cooldown, 6 issues/hour.
