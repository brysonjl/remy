# Security policy

Remy handles microphone audio, transcripts, clipboard delivery, Accessibility
metadata, and optional provider credentials. Treat reports involving unintended
capture/sharing, credential exposure, unsafe paste, or malicious model/update
artifacts as security reports.

## Report privately

Use GitHub's [private vulnerability reporting form](https://github.com/brysonjl/remy/security/advisories/new)
when it is available. If private reporting is unavailable, open an issue asking
for a private reporting channel **without describing the vulnerability or posting
sensitive evidence**. Do not send a vulnerability report to upstream maintainers
unless you have confirmed the issue also affects their project.

Include the affected commit/version, macOS and hardware, expected versus observed
behavior, and a minimal reproduction using synthetic data. Redact credentials,
personal audio/text, clipboard contents, titles, URLs, and signing information.
Share only the evidence needed to reproduce the issue. Do not test against other
people's data, accounts, or running apps without their permission.

## Supported versions and disclosure

Fixes target the main branch and the next release; the current release is the
0.1.0 early beta prerelease. There is no published security support window for
older builds, which should be reproduced against current source where practical.
Maintainers will triage reports and coordinate a fix and public advisory when
appropriate. There is no promised response deadline, bounty program, or guarantee
of a fix.

Private vulnerability reporting is enabled for this repository. See
[GitHub's configuration guidance](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)
for how it is set up.
