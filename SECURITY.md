# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through GitHub instead:
on this repository's **Security** tab, choose **Report a vulnerability**.

Include the SDK and version, what an attacker could do, and steps or code to reproduce it. We will reply
within 3 working days, keep you updated while we work on a fix, and credit you in the release notes if
you'd like.

A problem in the PageRush API itself, rather than in one of these libraries, can be reported the same way.

## Supported versions

Security fixes go into the latest release of each SDK. While an SDK is `0.x`, upgrade to its newest
`0.x` release to get them.

## Handling secrets

The SDKs read your API key from the `PAGERUSH_API_KEY` environment variable when you don't pass one,
and never log it. Keep the API key and webhook secret out of source control and client-side code.
