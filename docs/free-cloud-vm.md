# Run custom-airp on a free Google Cloud VM

Hosting airp on a small cloud machine that is always on, so a story can be reached from
any device at any hour, without paying for the machine. This page covers Google Cloud's
Always Free tier; the airp side is the same as any Linux install, and the ordinary manual
still applies once it is running. For running it on WSL, Android, or a machine you already
own, see [PORTABLE.md](PORTABLE.md).

Everything here was done against a real VM. The free tier has exact conditions, and the
console's defaults break several of them; where that happens this page says so.

---

## Contents

1. [What you get](#what-you-get)
2. [The free-tier rules](#the-free-tier-rules)
3. [Create the VM](#create-the-vm)
4. [First login](#first-login)
5. [Reaching it with Tailscale](#reaching-it-with-tailscale)
6. [Install and run airp](#install-and-run-airp)
7. [Keeping it up to date](#keeping-it-up-to-date)
8. [The stories in a browser](#the-stories-in-a-browser)
9. [Check that it is free](#check-that-it-is-free)
10. [A safety net](#a-safety-net)

---

## What you get

One `e2-micro` machine — 2 shared vCPUs and 1 GB of RAM — with a 30 GB standard disk,
running 24 hours a day, at $0 a month when it is set up exactly as below. That is a small
machine, and airp is a small program: the model runs at the provider, and what stays on the
VM is a terminal interface, an SQLite file and the tokenizer.

You need a Google account and a billing account with a card on it. The card is required even
though nothing is meant to be charged to it, which is what [the safety net](#a-safety-net)
is for.

---

## The free-tier rules

The free tier is a discount applied to specific things, not a plan you choose. Any of these
not matching means the VM is billed at the ordinary rate.

- **Machine type exactly `e2-micro`.** `e2-custom-2-1024` looks identical in every column and
  costs about $35 a month.
- **Region `us-west1`, `us-central1` or `us-east1`.** Any zone in them.
- **Boot disk `pd-standard`** — Standard persistent disk — up to 30 GB. The console defaults
  to Balanced, which is not free.
- **One `e2-micro` per billing account.** A second one is paid for.
- **No snapshot schedule and no Ops Agent.** The console switches both on by default; turn
  them off.
- **Outbound traffic up to 1 GB a month**, excluding China and Australia. Traffic through
  Tailscale counts too. A terminal session is small — text in, text out — and the model's
  replies come in rather than go out, so airp alone will not reach it.

**The console's estimate will say about $6.11 a month.** It ignores the free tier. The
discount appears only on the bill, as a *Free Tier* credit against each charge, which is why
[checking](#check-that-it-is-free) is a step of its own.

---

## Create the VM

The command below is the tested one. It runs in **Cloud Shell** — the `>_` button at the top
of the console, which needs nothing installed — or in a local `gcloud`. Replace `PROJECT_ID`
and `VM_NAME`.

```bash
gcloud compute instances create VM_NAME \
  --project=PROJECT_ID --zone=us-central1-f --machine-type=e2-micro \
  --network-interface=network-tier=PREMIUM,stack-type=IPV4_ONLY,subnet=default \
  --maintenance-policy=MIGRATE --provisioning-model=STANDARD \
  --create-disk=auto-delete=yes,boot=yes,device-name=VM_NAME,image-family=debian-13,image-project=debian-cloud,mode=rw,size=30,type=pd-standard \
  --no-shielded-secure-boot --shielded-vtpm --shielded-integrity-monitoring
```

Every free-tier condition is in there by name: `e2-micro`, a zone in `us-central1`, a 30 GB
`pd-standard` disk, and no snapshot schedule or agent because nothing asked for one.

**In the console instead:** choose **E2**, then **Preset** and `e2-micro` — not Custom — set
the boot disk type to **Standard persistent disk**, and turn off the snapshot schedule and
the Ops Agent before creating.

**On Windows without Cloud Shell:** `winget install Google.CloudSDK`, then `gcloud init`.
PowerShell reads the command differently — variables are `$VAR = "..."`, and lines continue
with a backtick rather than a backslash.

---

## First login

```bash
gcloud compute ssh VM_NAME --zone=us-central1-f
```

This creates the SSH key and the user on first use; there is no password, and none is needed.

1 GB of RAM is tight, so give the machine 2 GB of swap before installing anything:

```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
```

```bash
sudo mkswap /swapfile && sudo swapon /swapfile
```

```bash
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

The last line makes it survive a reboot.

---

## Reaching it with Tailscale

Optional but recommended. `gcloud compute ssh` works fine on its own; what Tailscale adds is
a stable private address that every one of your devices can reach — a tablet included —
without port 22 facing the internet.

```bash
curl -fsSL https://tailscale.com/install.sh | sh
```

```bash
sudo tailscale up --ssh
```

It prints a link; open it and approve the machine. `--ssh` lets Tailscale handle SSH
authentication, so there are no keys to copy anywhere. From then on, from any device on
your tailnet:

```bash
ssh -t USER@100.x.y.z
```

where `USER` is what `whoami` says on the VM and the address is the VM's Tailscale one.

Once that works, close the public door: remove the firewall rule that opens port 22 to the
internet, and only the tailnet reaches the machine. Keep the VM's external address — it is
what the VM itself uses to reach Tailscale and the model provider.

---

## Install and run airp

The same steps as any Linux machine without the SDK; the release carries a `linux-x64`
binary, which is what an `e2-micro` needs. Debian's image lacks two things .NET wants — ICU
for text handling and the certificates HTTPS to the provider needs:

```bash
sudo apt update && sudo apt install -y libicu-dev ca-certificates
```

Download the latest release and put it on the path:

```bash
curl -fL -o airp.tar.gz https://github.com/argamboad/custom-airp/releases/latest/download/airp-linux-x64.tar.gz
```

```bash
tar xzf airp.tar.gz && sudo mv airp /usr/local/bin/ && rm airp.tar.gz && airp version
```

The first run takes a few seconds longer: the single file unpacks itself once.

**The key.** There is no DPAPI on Linux, so `airp secret set` refuses; the store reads an
environment variable of the same name instead. Set it so that it never touches your shell
history:

```bash
read -rs -p "OpenRouter key: " k && echo "export OPENROUTER_API_KEY='$k'" >> ~/.bashrc && unset k
```

```bash
source ~/.bashrc && airp secret show
```

That leaves the key readable to anything running as your user on the VM, which is weaker
than the Windows store. A key created just for this machine, with a credit limit set on
it at the provider, is the right shape: if the VM is ever compromised, that one key is
revoked and nothing else changes.

**Check that it answers**, then start:

```bash
airp ask "Say something."
```

```bash
airp library --samples
```

```bash
airp
```

**Run it inside `tmux`.** A connection from a tablet drops; a session inside `tmux` does
not go with it. `C` copies to your local clipboard through the terminal, and `tmux` needs
to be told to pass that on:

```bash
sudo apt install -y tmux && echo 'set -g set-clipboard on' >> ~/.tmux.conf
```

```bash
tmux
```

From another machine, in one line — `-t` because the interface needs a real terminal, and
`bash -ic` because the key lives in `~/.bashrc`:

```bash
ssh -t USER@100.x.y.z 'bash -ic airp'
```

Bringing stories from another install, and why one story should live on one machine only,
is [PORTABLE.md](PORTABLE.md#moving-the-data).

**What is on that disk.** The database holds the whole history in the clear, and the key is
in a text file. Google encrypts the disk at rest, but it is still someone else's machine.
Decide that before moving a real story there.

---

## Keeping it up to date

The VM can install each new release by itself. It **pulls**: a timer on the machine asks
GitHub for the latest release every hour and installs it if it is newer. Nothing reaches in
from outside — no deploy job, no key to the machine sitting in anyone's CI — which matters
all the more because the repository is public.

A release is installed only if its download matches the `SHA256SUMS` file published with it;
a release without one is refused rather than installed unchecked. Releases after v1.2.0
publish it. The script never downgrades and never touches your data, and it replaces the
binary by renaming a new one over it, so a session left open keeps running on the version it
started with and the next one gets the new version.

The script, as `/usr/local/sbin/airp-update` — a fork changes `repo` to its own:

```sh
#!/bin/sh
# Installs the newest airp release when it is newer than the one installed,
# and only once the download matches the release's own SHA256SUMS.
set -eu

repo=argamboad/custom-airp
asset=airp-linux-x64.tar.gz
target=/usr/local/bin/airp

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# The binary unpacks part of itself on first run; give it somewhere to do that
# that exists under systemd, where root may have no HOME.
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work/.bundle"

latest=$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" \
  | sed -n 's/.*"tag_name": *"v\([^"]*\)".*/\1/p' | head -n 1)
installed=$("$target" version 2>/dev/null | awk '{print $2}' | cut -d+ -f1)

if [ -z "$latest" ]; then
  echo "airp-update: could not read the latest release" >&2
  exit 1
fi

newest=$(printf '%s\n%s\n' "$installed" "$latest" | sort -V | tail -n 1)
if [ "$installed" = "$latest" ] || [ "$newest" != "$latest" ]; then
  exit 0
fi

cd "$work"
base="https://github.com/$repo/releases/download/v$latest"

if ! curl -fsSL -o SHA256SUMS "$base/SHA256SUMS"; then
  echo "airp-update: v$latest publishes no SHA256SUMS; not installing it" >&2
  exit 1
fi

curl -fsSL -o "$asset" "$base/$asset"

if ! grep -E " [ *]$asset\$" SHA256SUMS | sha256sum -c - >/dev/null; then
  echo "airp-update: the v$latest download does not match its checksum; not installing it" >&2
  exit 1
fi

tar xzf "$asset"

if ! ./airp version | grep -Eq "^airp $latest([+ ]|\$)"; then
  echo "airp-update: the v$latest download reports another version; not installing it" >&2
  exit 1
fi

# Beside the old one and then renamed over it: a session that is open keeps the
# binary it started with, and the next one gets this.
install -m 755 airp "$target.new"
mv -f "$target.new" "$target"
echo "airp-update: $installed -> $latest"
```

Make it executable:

```bash
sudo chmod 755 /usr/local/sbin/airp-update
```

A service that runs it, as `/etc/systemd/system/airp-update.service`:

```ini
[Unit]
Description=Install the newest airp release
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/airp-update
```

And a timer that runs the service every hour, as `/etc/systemd/system/airp-update.timer`:

```ini
[Unit]
Description=Look for a new airp release every hour

[Timer]
OnBootSec=5min
OnUnitActiveSec=1h
RandomizedDelaySec=5min
Persistent=true

[Install]
WantedBy=timers.target
```

Switch it on:

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now airp-update.timer
```

When it last ran, when it runs next, and what it said:

```bash
systemctl list-timers airp-update.timer
```

```bash
journalctl -u airp-update.service -n 20
```

An hourly check is one small request to GitHub's API, well inside the sixty an hour it allows
without a login; the download, about 45 MB, happens only when there is something new.

---

## The stories in a browser

Optional: the stories as web pages, for playing from a phone without a terminal — what they do
is in the manual, [Playing from a browser](MANUAL.md#playing-from-a-browser). On this machine
they run as a service beside airp and are reached the same private way.

The releases do not carry the pages, and the updater above does not touch them. Build them on a
machine with the .NET SDK, from a clone of the repository, and copy the folder over:

```bash
dotnet publish src/Airp.Web -c Release -r linux-x64 --self-contained -o airp-web
```

```bash
scp -r airp-web USER@100.x.y.z:
```

On the VM, put it in place:

```bash
sudo rm -rf /usr/local/lib/airp-web && sudo mv ~/airp-web /usr/local/lib/airp-web && sudo chmod +x /usr/local/lib/airp-web/airp-web
```

The pages need the model key for the turns they send. A service does not read `~/.bashrc`, so
the key goes in a file only your user can read, written without passing through your shell
history:

```bash
mkdir -p ~/.config/airp-web && read -rs -p "OpenRouter key: " k && printf 'OPENROUTER_API_KEY=%s\n' "$k" > ~/.config/airp-web/env && unset k && chmod 600 ~/.config/airp-web/env
```

The service, as `/etc/systemd/system/airp-web.service` — with your user, and your Tailscale
login as the one account let in:

```ini
[Unit]
Description=airp web pages (localhost only, reached through tailscale serve)
Wants=network-online.target
After=network-online.target

[Service]
User=USER
WorkingDirectory=/usr/local/lib/airp-web
Environment=Airp__Web__Login=you@example.com
EnvironmentFile=/home/USER/.config/airp-web/env
ExecStart=/usr/local/lib/airp-web/airp-web --urls http://127.0.0.1:5291
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now airp-web
```

And give it an address on your tailnet — on 8443, so 443 stays free for anything else:

```bash
sudo tailscale serve --bg --https=8443 http://127.0.0.1:5291
```

`https://<vm-name>.<tailnet>.ts.net:8443/` on the phone, with Tailscale on, is your stories.
`journalctl -u airp-web -n 20` says why when it is not: it refuses to start without a login
to let in, and turns away anyone else with "Not available."

**It fits, not by much.** The pages hold 100–215 MB of the `e2-micro`'s 1 GB while running, and
airp open in `tmux` beside them about 270 MB, which leaves around 200 MB. A third .NET process
does not fit: measured on 2026-10-01, with one more beside them the VM went into swap, spent
80% of its time waiting on the disk, and a paste in the composer arrived a character at a time.
If typing ever crawls there, `free -m` and `vmstat 1` say whether this is why.

To update them, publish and copy again, move the folder into place, then
`sudo systemctl restart airp-web`.

---

## Check that it is free

Right after creating, confirm the shape of what exists:

```bash
gcloud compute instances list --project=PROJECT_ID --format="table(name,zone.basename(),machineType.basename(),status)"
```

```bash
gcloud compute disks list --project=PROJECT_ID --format="table(name,sizeGb,type.basename(),resourcePolicies)"
```

```bash
gcloud compute snapshots list --project=PROJECT_ID
```

```bash
gcloud compute resource-policies list --project=PROJECT_ID
```

```bash
gcloud compute addresses list --project=PROJECT_ID
```

Expected: one `e2-micro`, one 30 GB `pd-standard` disk with nothing in its policies column,
and the last three lists empty. A snapshot, a resource policy or a reserved address is
something that costs money and was not asked for.

Then, after 24 to 48 hours, the bill itself: **Billing → Reports**, grouped by SKU. Each E2
and PD charge should have a *Free Tier* credit beside it for the same amount, and the net
total should read $0.00.

---

## A safety net

A budget alert at $1, so a mistake is an email rather than a surprise. Budgets count cost
after credits by default, so this fires only on real charges.

```bash
gcloud services enable billingbudgets.googleapis.com --project=PROJECT_ID
```

The billing account's id:

```bash
gcloud billing projects describe PROJECT_ID --format="value(billingAccountName)"
```

```bash
gcloud billing budgets create --billing-account=BILLING_ACCOUNT_ID \
  --display-name="free-tier-guard" --budget-amount=1USD \
  --filter-projects="projects/PROJECT_ID" \
  --threshold-rule=percent=0.01 --threshold-rule=percent=0.5 --threshold-rule=percent=1.0
```

The three thresholds mean an email at one cent, at fifty cents and at a dollar. If the
billing account is not in US dollars, change the currency to match.
