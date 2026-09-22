# Running airp somewhere else

Installing on a machine that is not the one you built it on: WSL, a Linux box, an Android
tablet, and reaching any of them from your desk. For the ordinary install see
[MANUAL.md](MANUAL.md#installing); this page is about the parts that only come up when the
machine changes.

Everything here was done against real installs. Where something is untested it says so.

---

## Contents

1. [What the platform decides](#what-the-platform-decides)
2. [WSL](#wsl)
3. [Android, through Termux](#android-through-termux)
4. [Moving the data](#moving-the-data)
5. [One story, one copy](#one-story-one-copy)
6. [Reaching it from another machine](#reaching-it-from-another-machine)

---

## What the platform decides

Three things change with the platform, and nothing else does.

**The API key.** On Windows the key is encrypted against your account and `airp secret set`
puts it there. Everywhere else that command refuses: the encryption is DPAPI, which is a
Windows facility. On Linux and macOS the key comes from an environment variable of the same
name, `OPENROUTER_API_KEY` by default — a fallback the store has always had, not a workaround.
`airp secret` says which of the two is answering. Set it in a way that keeps it out of your
shell history:

```bash
read -rs -p "OpenRouter key: " k && echo "export OPENROUTER_API_KEY='$k'" >> ~/.bashrc && unset k
```

That leaves the key readable to anything running as you. It is weaker than the Windows store,
and on a shared machine it is worth a password manager or a keyring instead.

**Where the data lives.** `%LOCALAPPDATA%\Airp` on Windows, `~/.local/share/Airp` on Linux and
macOS. `AIRP_HOME` overrides it anywhere.

**The clipboard.** `C` needs a clipboard to exist. Under WSL the copy goes to the Windows
clipboard and works; in a container or a `proot` sandbox with no display server there is
nothing to copy to, and the copy is reported as rejected rather than pretending to succeed.

---

## WSL

A normal Linux install that happens to sit inside Windows. Ubuntu's own packages have the SDK:

```bash
sudo apt install -y dotnet-sdk-10.0
```

Then clone and install as a global tool, which puts `airp` on your path:

```bash
git clone https://github.com/argamboad/custom-airp ~/src/custom-airp
cd ~/src/custom-airp
dotnet pack src/Airp.Terminal -c Release
dotnet tool install --global --add-source ./src/Airp.Terminal/bin/Release --prerelease Airp.Terminal
```

`~/.dotnet/tools` has to be on your `PATH`; the installer prints as much if it is not:

```bash
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.bashrc
```

**Clone with tags.** The version comes from the nearest `v*` tag, so a shallow clone leaves
nothing to find and the build calls itself `0.0.0`.

**Updating** is the same three commands with `dotnet tool update`, and it fails while a session
is open — the binary is locked. That is not a build problem.

---

## Android, through Termux

airp is a .NET program, and .NET needs glibc. Termux is Android, which uses bionic, so airp
cannot run in Termux directly. It runs in a small glibc Linux that Termux hosts through
`proot-distro`. Expect it to be slower than a laptop: every system call goes through a
translation layer.

There is a second problem. The release binaries cover `win-x64`, `linux-x64`, `osx-arm64` and
`osx-x64` — no `linux-arm64`, which is what a phone or tablet is. Build one on any machine with
the SDK, the same way [the release workflow](../.github/workflows/release.yml) does:

```bash
dotnet publish src/Airp.Terminal -c Release -r linux-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

One self-contained file, around 43 MB, needing no SDK on the device. Get it onto the tablet
however you like — `scp` from the machine that built it is easiest.

On the device, in Termux:

```bash
pkg install proot-distro openssh
proot-distro install ubuntu
proot-distro login ubuntu
```

Then inside that Ubuntu:

```bash
apt update && apt install -y libicu-dev ca-certificates openssh-client
mv airp /usr/local/bin/ && airp version
```

ICU is what .NET uses for text handling, and the certificates are what HTTPS to the model
provider needs. Set the key as above, and `airp` runs.

**If it dies at startup** complaining about memory mapping or W^X, `proot`'s memory rules are
the likely cause:

```bash
export DOTNET_EnableWriteXorExecute=0
```

---

## Moving the data

The data folder is portable: a database file, a JSON file and four folders of plain text. Two
things should not travel:

- **`secrets/`** — encrypted against a Windows account, so it is unreadable anywhere else and
  pointless to copy. The destination uses an environment variable instead.
- **`logs/`** — rolling files that belong to the machine that wrote them.

**Close airp before copying the database.** SQLite keeps recent writes in a write-ahead log,
`airp.db-wal`, and folds it in when the application closes. Copy `airp.db` alone while a
session is open and you get a database missing your latest turns — the copy looks complete and
is quietly out of date. If `airp.db-wal` and `airp.db-shm` are absent, everything is in the
database.

```bash
tar czf airp-data.tar.gz --exclude=secrets --exclude=logs -C ~/.local/share/Airp .
```

```bash
mkdir -p ~/.local/share/Airp && tar xzf airp-data.tar.gz -C ~/.local/share/Airp
airp config
```

`airp config` is the confirmation: it prints the budget and the settings from the file you just
unpacked, so a wrong path shows up immediately rather than as an empty chat list.

---

## One story, one copy

Copying the data does not link the two machines. Each database only knows what was played on
it, and **there is no merge**: `Messages` is append-only by design, both sides append their own
turns, and nothing reconciles two divergent histories.

So pick one machine per story. A pattern that works: one install is the real one, the others
are for reading, or for stories that live only there. The interface shows the chat, not the
machine, so the mistake is easy to make and impossible to undo.

If you want one story on several machines, do not copy — keep it on one and reach it over SSH.

---

## Reaching it from another machine

airp is a terminal application, so SSH is the whole answer. A mesh VPN such as Tailscale is
what makes the addresses stable and the connection private without opening a port to the
internet.

```bash
ssh -t user@host airp
```

**`-t` matters.** The interface needs a real terminal; without it you get a program drawing
into a pipe.

**So does the shell being interactive**, when the key is in `~/.bashrc`:

```bash
ssh -t user@host 'bash -ic airp'
```

Otherwise airp starts with no key. `airp secret` over the same connection says which it found.

Reaching an install that is itself inside something adds one hop. Under WSL the host is
Windows, and Windows starts the distribution:

```bash
ssh -t user@windows-host wsl -d Ubuntu-26.04 -e bash -ic airp
```

On Android, Termux's SSH server listens on **8022**, and the Ubuntu is another layer in:

```bash
ssh -p 8022 -t user@tablet 'proot-distro login ubuntu -- bash -ic airp'
```

An `~/.ssh/config` entry turns any of these into one word:

```
Host story
    HostName tablet
    Port 8022
    User u0_a000
    RequestTTY yes
    RemoteCommand proot-distro login ubuntu -- bash -ic airp
```

**Worth knowing before you rely on it:**

- **Key authentication, not passwords.** A password prompt where you expected none usually
  means the user name is wrong, so the key was never considered.
- **Android does not keep servers running.** `sshd` does not survive a reboot, and the system
  may stop it while the screen is off. `termux-wake-lock`, exemption from battery optimisation
  and the Termux:Boot add-on all help; none of them make it reliable.
- **The host has to be awake.** A sleeping laptop is an unreachable one.
- **Two sessions, one database.** Connecting from elsewhere does not open a second copy. Do not
  leave a session open locally and then join from another machine to play the same story.
- **SSH encrypts the session, and that is all it does.** Routing through a machine still means
  passing through it. If a machine is monitored, tunnelling through it does not make it not
  monitored.
