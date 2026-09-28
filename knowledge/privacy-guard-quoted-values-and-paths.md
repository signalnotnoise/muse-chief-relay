---
id: privacy-guard-quoted-values-and-paths
title: PrivacyGuard reads quoted values and file names
summary: A short first word inside quotes no longer hides an assignment, and Scan checks the relative path.
tags: [privacy, knowledge]
source: alex
authors: [chief]
created: 2026-09-28
visibility: public
---

Issues #25 and #26. Password, token, and trip-password assignments used to stop the value at the first space, so a quoted secret whose first word was shorter than the minimum could pass. The value is now the whole quoted span, and the minimum length applies to that span. Unquoted values still end at whitespace.

Scan also runs those patterns on the note's relative path. A bearer key, token, assignment, or raw board path in a file name or directory segment fails closed even when the body is clean. A 64-character lowercase hex board file name, and the documentation placeholder, stay allowed. Findings still name the kind of secret and do not copy the secret into the message.
