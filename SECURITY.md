# Security policy

## Supported versions

Security fixes go into the latest released version. Please update before reporting.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Use GitHub's private reporting instead: open the repository's **Security** tab, choose **Report a vulnerability**
(a private security advisory). Include the Rotula version, what you did, what happened and, if you can, a sample file
(`.rotula`, `.zpl`, `.csv` or `.xlsx`) that shows the problem.

You can expect an acknowledgement within a few days and a status update as the fix progresses. This is a volunteer project,
so there are no guaranteed timelines, but security reports are handled before feature work. You will be credited in the
changelog unless you prefer otherwise.

## What is in scope

Rotula is an offline desktop application. It makes no network calls except the printing you ask for (a TCP connection to the
printer address you enter), the databases you connect, and, only when you or your administrator switch it on, one anonymous
daily request to the GitHub "latest release" API (see [docs/deployment.md](docs/deployment.md#network-traffic-and-the-update-check)). Reports of interest include:

- Crashes, hangs or memory exhaustion when opening a crafted `.rotula`, ZPL, CSV or Excel file.
- Anything that causes Rotula to write outside the folders you chose, or to send data anywhere other than the printer you selected.
- Problems in the release packages (for example a mismatch with the published `SHA256SUMS.txt`).

Release builds are not code-signed; see the README for what to expect from Windows SmartScreen.
