# Changelog

## [0.2.3](https://github.com/odata2ts/test-server-asp-net/compare/v0.2.2...v0.2.3) (2026-08-25)


### Bug Fixes

* enforce the optimistic concurrency Copies announces ([#27](https://github.com/odata2ts/test-server-asp-net/issues/27)) ([de734d9](https://github.com/odata2ts/test-server-asp-net/commit/de734d93e1628416eb1bd6374a42af209894fafe))

## [0.2.2](https://github.com/odata2ts/test-server-asp-net/compare/v0.2.1...v0.2.2) (2026-08-20)


### Bug Fixes

* do not read a multipart $batch body as requests of its own ([#22](https://github.com/odata2ts/test-server-asp-net/issues/22)) ([dee468d](https://github.com/odata2ts/test-server-asp-net/commit/dee468db71466f982006f1e81bec1664b4796699))

## [0.2.1](https://github.com/odata2ts/test-server-asp-net/compare/v0.2.0...v0.2.1) (2026-08-20)


### Bug Fixes

* let a client delete a branch it created ([05aef95](https://github.com/odata2ts/test-server-asp-net/commit/05aef9558db15e850457cb7b5c5532a6534f9ab5))

## [0.2.0](https://github.com/odata2ts/test-server-asp-net/compare/v0.1.0...v0.2.0) (2026-08-20)


### Features

* annotate the generated keys and let the client assign Branch ([f896c46](https://github.com/odata2ts/test-server-asp-net/commit/f896c46fabee7640bd4d4174c89e85bb10694c35))
* enforce the managed-property annotations ([341c476](https://github.com/odata2ts/test-server-asp-net/commit/341c476795e4b112e1d99a13ae33fee97f57c6f9))

## 0.1.0 (2026-08-19)


### Features

* accept query options in the request body ([#2](https://github.com/odata2ts/test-server-asp-net/issues/2)) ([b702377](https://github.com/odata2ts/test-server-asp-net/commit/b702377303a03c7e78346ed57e2f9e5b90ad520a))
* add tet harness to verify the running server ([7acdaed](https://github.com/odata2ts/test-server-asp-net/commit/7acdaed4d04e7bd09defbe7aa454e58ac893d32a))
* annotate Loan.LoanedAt as Core.Immutable ([5f7957a](https://github.com/odata2ts/test-server-asp-net/commit/5f7957a7ec30ca9c7d730de63e247ae9de06a871))
* annotate Member.ActiveSince and Member.Balance ([930003a](https://github.com/odata2ts/test-server-asp-net/commit/930003a4bd03b64e3a09a23600a859167ef870ba))
* back the service with PostgreSQL instead of in-memory SQLite ([56dfebb](https://github.com/odata2ts/test-server-asp-net/commit/56dfebb6c0b04a6b6b86a0e2378f75688a7e7114))
* declare OData vocabulary annotations on the model ([#9](https://github.com/odata2ts/test-server-asp-net/issues/9)) ([1f7adf9](https://github.com/odata2ts/test-server-asp-net/commit/1f7adf975890c69e5ed6cba38f263b271c0fa3bb))
* deep insert and 4.01 delta payloads ([f51f05d](https://github.com/odata2ts/test-server-asp-net/commit/f51f05d73bc0a3e224720a4383ebf31f36e9567a))
* derive annotations and facets from the EF Core model ([1f7adf9](https://github.com/odata2ts/test-server-asp-net/commit/1f7adf975890c69e5ed6cba38f263b271c0fa3bb))
* make $search actually search, and enable $batch ([251f294](https://github.com/odata2ts/test-server-asp-net/commit/251f294b678e9b58c72f01eb19513498659bd275))
* media entity streams and $ref ([2e19372](https://github.com/odata2ts/test-server-asp-net/commit/2e19372c5ac4de87bc64885949a7f76193e385d4))
* model and EDM for the "Library" reference model ([4f43cc8](https://github.com/odata2ts/test-server-asp-net/commit/4f43cc87aca1d3ac8a7d23ab4cbcea2538bc7087))
* partner, alternate keys and search restrictions in the emitted model ([#1](https://github.com/odata2ts/test-server-asp-net/issues/1)) ([f5ab1c0](https://github.com/odata2ts/test-server-asp-net/commit/f5ab1c0af6e38c914516bce4d54b1dcfdc702914))
* rewritten feature coverage ([b781ed5](https://github.com/odata2ts/test-server-asp-net/commit/b781ed5c3ec176e80219fd30908dd340a0bbe80b))
* rewritten feature coverage ([f3e4ab4](https://github.com/odata2ts/test-server-asp-net/commit/f3e4ab4889a9c43aeb855d8b868c1d70754c3c5c))
* seed data, entity sets and all 29 operations ([aac4164](https://github.com/odata2ts/test-server-asp-net/commit/aac41641fb64e4fb8c1f9925deabebf3c6aff65d))
* test scripts ([aeb05d1](https://github.com/odata2ts/test-server-asp-net/commit/aeb05d1c355243598cd9069435bf61cfe1ab09b6))
* use SQLite as in-memory DB ([621a07b](https://github.com/odata2ts/test-server-asp-net/commit/621a07b1bcf44ab9f5847c721afb5c80f5aad183))


### Bug Fixes

* answer 400 instead of 500 for a missing required action parameter ([d7bd978](https://github.com/odata2ts/test-server-asp-net/commit/d7bd978bf9ca018964e4873a68cb41805a7752a4))
* apply MaxResults on the second Search overload ([70517a9](https://github.com/odata2ts/test-server-asp-net/commit/70517a92c530cf6bbc8c9a33a18806c8c35e2e68))
* bind existing entities without corrupting the store ([ebb700d](https://github.com/odata2ts/test-server-asp-net/commit/ebb700de2f5fb27b2572b0eec8c6331a00de2f67))
* **ci:** smoke-test an entity set this model actually has ([dfa015c](https://github.com/odata2ts/test-server-asp-net/commit/dfa015caffc0206f2361ee4a121ad7823e92bd9f))
* do not compare an Edm.Date literal against a timestamp ([56dfebb](https://github.com/odata2ts/test-server-asp-net/commit/56dfebb6c0b04a6b6b86a0e2378f75688a7e7114))
* no change tracking on reads & fix resolving ([6dfc969](https://github.com/odata2ts/test-server-asp-net/commit/6dfc969bb203c689ca3af16586056fec5e7c3bac))
* serve DELETE for streams and copies, and stop dropping copy properties ([#3](https://github.com/odata2ts/test-server-asp-net/issues/3)) ([76ed8df](https://github.com/odata2ts/test-server-asp-net/commit/76ed8df2bf4e22d562186387845ec5613c7422bb))
