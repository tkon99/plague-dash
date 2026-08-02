#!/usr/bin/env python3
"""Plague Dash release packager.

Builds the mod, packages a UMM-installable zip, bumps the version, tags, pushes
the tag (which triggers the GitHub Actions release workflow), and uploads the
zip to the resulting GitHub Release via the `gh` CLI.

The mod can't be built in CI — it compiles against Plague Inc's proprietary
Assembly-CSharp.dll and Unity engine DLLs, which can't go on a GitHub-hosted
runner. So the artifact is built locally, and CI only cuts the release shell;
this script attaches the binary.

Usage:
    python tools/release.py <version>      # e.g. 0.2.0
    python tools/release.py <version> --no-upload   # build + package + tag only

What it does:
  1. Runs `mod/build.bat /nodeploy` to produce a fresh PlagueDash.dll.
  2. Bumps <Version> in mod/Info.json to the given version.
  3. Stages PlagueDash.dll + Info.json into dist/PlagueDash-<version>.zip in the
     UMM-expected structure (PlagueDash/... so unzipping into Mods/ lands right).
  4. Commits the Info.json bump, tags v<version>, pushes main + the tag.
  5. Uploads the zip to the GitHub Release the workflow just created. Uses `gh`
     if authed, otherwise falls back to the git-credential-manager token via the
     API (so it works without `gh auth login`, as long as `git push` does).

Prerequisites:
  - VS 2022 Build Tools + UMM installed for Plague Inc (see mod/README.md).
  - git remote `origin` pointing at GitHub, with push credentials configured
    (the upload reuses git's stored token if `gh` isn't authed).
"""
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MOD = ROOT / "mod"
DIST = ROOT / "dist"
INFO = MOD / "Info.json"


def run(cmd, **kw):
    print(f"$ {' '.join(cmd) if isinstance(cmd, list) else cmd}")
    return subprocess.run(cmd, shell=isinstance(cmd, str), check=True, **kw)


def main():
    args = [a for a in sys.argv[1:] if a != "--no-upload"]
    do_upload = "--no-upload" not in sys.argv
    if not args:
        print(__doc__)
        sys.exit(1)
    version = args[0].lstrip("v")
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        sys.exit(f"Version must be semver like 0.2.0, got: {version}")

    print(f"=== Releasing Plague Dash v{version} ===")

    # 1. Build a fresh DLL (nodeploy — we package, not install).
    run(["cmd", "/c", "build.bat", "/nodeploy"], cwd=MOD)
    dll = MOD / "obj" / "Release" / "PlagueDash.dll"
    if not dll.exists():
        sys.exit(f"Build did not produce {dll}")
    print(f"  built: {dll} ({dll.stat().st_size:,} bytes)")

    # 2. Bump Info.json version.
    info = json.loads(INFO.read_text(encoding="utf-8"))
    info["Version"] = version
    INFO.write_text(json.dumps(info, indent=2) + "\n", encoding="utf-8")
    print(f"  bumped Info.json -> {version}")

    # 3. Package into the UMM-expected structure (PlagueDash/...).
    DIST.mkdir(exist_ok=True)
    zip_name = DIST / f"PlagueDash-{version}.zip"
    with zipfile.ZipFile(zip_name, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(dll, "PlagueDash/PlagueDash.dll")
        z.write(INFO, "PlagueDash/Info.json")
    print(f"  packaged: {zip_name} ({zip_name.stat().st_size:,} bytes)")

    # 4. Commit the version bump, tag, push.
    run(["git", "add", str(INFO.relative_to(ROOT))], cwd=ROOT)
    diff = subprocess.run(["git", "diff", "--cached", "--quiet"], cwd=ROOT).returncode
    if diff != 0:
        run(["git", "commit", "-m", f"Release v{version}"], cwd=ROOT)
    tag = f"v{version}"
    run(["git", "tag", "-f", tag], cwd=ROOT)
    run(["git", "push", "origin", "main", tag], cwd=ROOT)
    print(f"  pushed tag {tag} — release workflow is running.")

    if do_upload:
        # 5. Wait for the workflow to create the release shell, then attach the zip.
        print("\n  Waiting for the GitHub Release to be created by CI...")
        import time

        repo = _repo_slug()
        api = f"https://api.github.com/repos/{repo}/releases/tags/{tag}"
        release_id = None
        for _ in range(60):  # up to ~5 min
            token = _gh_token()
            req = urllib.request.Request(api, headers=_auth_headers(token))
            try:
                with urllib.request.urlopen(req, timeout=10) as r:
                    release_id = json.load(r).get("id")
                    break
            except urllib.error.HTTPError as e:
                if e.code != 404:
                    raise
            except Exception:
                pass
            time.sleep(5)
        if release_id is None:
            sys.exit(f"Release {tag} did not appear in time. Attach manually:\n"
                     f"  gh release upload {tag} {zip_name}")

        _upload_asset(repo, release_id, tag, zip_name)
        print(f"\n=== Released v{version}. ===")
        print(f"    {zip_name} attached to https://github.com/{repo}/releases/tag/{tag}")
    else:
        print(f"\n=== Built + packaged + tagged {tag} (not uploaded). ===")
        print(f"    {zip_name} ready; the tag push still triggered CI to cut the release.")
        print(f"    Attach manually if needed: gh release upload {tag} {zip_name}")


def _repo_slug():
    """Resolve the owner/repo slug from the git remote."""
    url = subprocess.check_output(
        ["git", "remote", "get-url", "origin"], cwd=ROOT, text=True).strip()
    # accept https://github.com/owner/repo(.git) or git@github.com:owner/repo(.git)
    import re
    m = re.search(r"github\.com[:/]([^/]+/[^/]+?)(?:\.git)?$", url)
    return m.group(1) if m else "tkon99/plague-dash"


def _gh_token():
    """A GitHub API token: prefer GH_TOKEN env, then git-credential-manager."""
    env = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if env:
        return env
    # Fall back to the token git itself uses to push (credential manager).
    try:
        cred = subprocess.run(
            ["git", "credential", "fill"],
            input="protocol=https\nhost=github.com\n\n",
            capture_output=True, text=True, timeout=10)
        for line in cred.stdout.splitlines():
            if line.startswith("password="):
                return line[len("password="):]
    except Exception:
        pass
    return None


def _auth_headers(token):
    h = {"Accept": "application/vnd.github+json"}
    if token:
        h["Authorization"] = f"token {token}"
    return h


def _upload_asset(repo, release_id, tag, zip_path):
    """Upload the zip to a release. Tries `gh` first, then the API directly
    (using the git-credential-manager token) so it works without `gh auth login`."""
    # Prefer the gh CLI if it's authed.
    if subprocess.run(["gh", "auth", "status"], capture_output=True).returncode == 0:
        run(["gh", "release", "upload", tag, str(zip_path), "--repo", repo, "--clobber"])
        return
    token = _gh_token()
    if not token:
        sys.exit(f"No GitHub token available (GH_TOKEN env empty, and `gh` not authed). "
                 f"Run `gh auth login`, then: gh release upload {tag} {zip_path}")
    url = (f"https://uploads.github.com/repos/{repo}/releases/{release_id}"
           f"/assets?name={zip_path.name}")
    with open(zip_path, "rb") as f:
        data = f.read()
    req = urllib.request.Request(
        url, data=data, method="POST",
        headers={**_auth_headers(token), "Content-Type": "application/zip"})
    with urllib.request.urlopen(req, timeout=60) as r:
        json.load(r)


if __name__ == "__main__":
    main()
