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
7. [Check that it is free](#check-that-it-is-free)
8. [A safety net](#a-safety-net)

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
