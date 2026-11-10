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

## Keep your runtime patched

The SDKs have no runtime dependencies. They use your language's own HTTP and TLS code: `urllib` and
`ssl` in Python, `fetch` in Node.js, the curl extension (and your system's libcurl) in PHP, and
`net/http` and `crypto/tls` in Go. Security fixes in those come from your runtime, not from an SDK
update, so:

- Use the latest patch release of your Python, Node.js, PHP or Go version, or your Linux
  distribution's security updates for it.
- The SDKs support older runtimes, such as Python 3.9 and PHP 8.1, because enterprise Linux
  distributions still ship and patch them. If your runtime no longer gets security updates from
  anyone, upgrade it.
- Go code is built with your Go toolchain: check your application with
  [`govulncheck`](https://go.dev/doc/security/vuln/).

## Handling secrets

The SDKs read your API key from the `PAGERUSH_API_KEY` environment variable when you don't pass one,
and never log it. Keep the API key and webhook secret out of source control and client-side code.
