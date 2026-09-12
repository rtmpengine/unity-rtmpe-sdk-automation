# RTMPE SDK License

**Version 2.0 — effective 2026-09-04**

Copyright (c) 2025-2026 RTMPE. All rights reserved.

This is a limited, service-linked license. It is not an open-source license.

---

## 1. Definitions

**"Software"** means this package — the RTMPE Unity SDK — including its source
code, compiled assemblies, editor tooling, analyzers, samples and documentation,
excluding the third-party components identified in section 6.

**"Service"** means the RTMPE networking service operated by RTMPE, and any
deployment of RTMPE's server software operated under a separate written
agreement with RTMPE; in either case reached through an RTMPE-issued API key.
A server that speaks the wire protocol without being RTMPE's server software, or
without such an agreement, is not the Service however it was built.

**"You"** means the individual or legal entity exercising the rights granted
below.

**"Good standing"** means that You hold a current RTMPE subscription that is
neither suspended nor closed, and that any amount due under it is not overdue
beyond any grace period RTMPE's subscription terms allow.

---

## 2. Grant

Subject to your continued compliance with this license and to an RTMPE account
in Good standing, RTMPE grants you a personal, non-exclusive, non-transferable,
non-sublicensable and revocable right to:

1. install and use the Software;
2. read and modify its source for your own integration; and
3. distribute the Software **only** as compiled into an application of yours
   that connects to the Service.

---

## 3. Restrictions

You may not:

1. **implement, host or operate a server** that speaks the RTMPE wire protocol,
   or assist a third party in doing so, whether derived from the Software, from
   its documentation, or from observation of its traffic — except a deployment
   that is the Service as section 1 defines it;
2. use the Software to connect to any service other than the Service;
3. redistribute, publish, sublicense, rent, lease or sell the Software as such,
   in source or binary form, separately from an application of yours. Sharing it
   inside your own organisation, or with a contractor working on your
   application under your direction, is not redistribution;
4. remove or obscure any copyright, license or attribution notice; or
5. use the Software after the rights granted in section 2 have ended.

---

## 4. Ownership

The Software is licensed, not sold. RTMPE retains all right, title and interest
in it, including the design of the wire protocol it implements. No rights are
granted by implication or estoppel.

---

## 5. Term and termination

The rights in section 2 continue while your RTMPE account is in Good standing.
They end automatically, without notice, on breach of section 3 or when the
account is closed or suspended. On termination you must stop using the Software
and destroy the copies in your possession; applications already distributed to
end users are not affected.

---

## 6. Third-party components

`Runtime/Infrastructure/Serialization/FlatBuffers/` is Google FlatBuffers,
licensed under the Apache License 2.0 and governed by the `LICENSE.txt` in that
directory rather than by this license. Nothing here narrows the rights that
license grants, and its notices must be preserved.

The Poly1305 arithmetic in `Runtime/Crypto/Internal/ChaCha20Poly1305Impl.cs`
follows the poly1305-donna 32-bit reference by Andrew Moon, which is placed in
the public domain. It carries no conditions, and its attribution in that file
must be preserved. Section 4 grants nothing by implication, so it is named here
rather than left to be inferred from the absence of a claim.

### Not third-party components

`Runtime/Infrastructure/Serialization/Generated/` is produced by Google's
`flatc` compiler from RTMPE's own schemas. The generated code is RTMPE's and is
governed by this license; only the FlatBuffers runtime it calls is the component
named above.

This section lists code **taken from** a third party. It is not a list of
influences: several files in this package implement a published specification or
match the observable behaviour of a reference implementation without copying it,
and say so in their own comments — among them the LZ4 block format
(`Runtime/Infrastructure/Compression/Lz4Compressor.cs`), the anti-replay window
of RFC 4303 §3.4.3 (`Runtime/Crypto/ReplayWindow.cs`), and the Curve25519,
Ed25519 and HKDF constructions of RFC 7748, RFC 8032 and RFC 5869
(`Runtime/Crypto/Internal/`). Those are RTMPE's implementations, governed by
this license, and no third-party license attaches to them by way of the
specification they follow.

---

## 7. Earlier versions

Versions of the Software published between 2026-05-22 and 2026-09-03 carried
the MIT license. Copies obtained under it remain governed by it, on their own
terms: a license already granted is not withdrawn here.

Versions published before 2026-05-22 were not MIT-licensed and nothing in this
section grants anything in respect of them. From 2026-04-29 the package carried
an express proprietary notice reserving all rights, and before that date it
carried no license file at all.

This license governs this version of the Software.

---

## 8. No warranty

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.

## 9. Limitation of liability

IN NO EVENT SHALL RTMPE BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

Questions about a use this license does not permit: contact RTMPE.
