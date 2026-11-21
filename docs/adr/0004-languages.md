# 0004. Languages and translations as a package, for more than one product

- **Status:** Accepted
- **Date:** 2026-11-21

## Context

A product on ABP that serves people in several languages needs the same things:

- the site's languages as data, each with its direction, its fallback, and whether it's on;
- the texts that ship with the code, translated or reworded by the site's admins without a new build;
- texts served to the web quickly, and every server agreeing at once when something changes.

ABP's open modules localize from resource files built into the app. Its Language Management module, which keeps
languages and texts in the database, is part of ABP's commercial tier. A first product had built its own: languages
in a table, admins' texts beside the shipped ones with a hash of the English they were made from, bundles cached by
version on each server, and an editor with export and import. A second product's languages were fixed in its web. Its
people wanted the same languages page, and the shared screens need one API to call.

## Decision

**Two packages: `AbpLanguages` and `AbpLanguages.HttpApi`.** This is the first product's model, made neutral.

- **Languages are data.** Each has:
  - a BCP 47 code, fixed once created;
  - a native name and an English name;
  - a direction, worked out from the code (`ar`, `fa`, `he`, `ur`…) and changeable;
  - an on/off switch and an order in the picker;
  - a fallback language.

  One language is the default. The source language (English) can be turned off but not removed: every text is
  written in it first. A new site gets the languages the host names (`AbpLanguagesOptions.Seed`, English alone by
  default).
- **Chains.** Anything shown goes through a chain: the language asked for, its fallbacks in turn, the default, then
  the source. An `Accept-Language` header picks the first enabled language it names: exactly, by its base language, or
  a regional language of that base.
- **Texts are catalogs that ship with code.** The host gives them through `ITextCatalogSource`. Each catalog has a
  name, and per language a flat map of dotted keys; the source language's keys are the catalog's keys. A source whose
  catalogs change while the site runs (a plugin turned on) says so with a generation number, and the texts are built
  again. Admins add texts per language, each kept with a hash of the source text it was made from, so a text whose
  source changed shows as out of date. A translation must keep the source's ICU arguments (`{name}`,
  `{count, plural, …}`), or it's refused.
- **Bundles, cached on every server.** Each language's merged texts are built once and kept as bytes with a content
  hash. Merged means: the admin's text, else the shipped one, along the chain, else the source. Servers agree through
  one row whose version goes up in the same transaction as any change. Each server reads it at most every two seconds,
  so no distributed cache is needed. A request with the current hash (`?v=`) is cached for good; one without it is
  answered by its ETag. The JSON's shape is the host's choice (`ITextBundleWriter`): by default the main catalog is
  `messages` and the others are under `catalogs`.
- **The tables are the host's**, through `ILanguagesDbContext` and `modelBuilder.ConfigureLanguages(...)` with the
  schema and table names it wants, as ABP modules do. So a product that had its own tables keeps them, rows and all.
- **The API** (`AbpLanguages.HttpApi`) is under the host's route prefix:
  - **Read by anyone**, since a sign-in page needs its texts: the enabled languages, each with its texts' hash, and a
    language's bundle.
  - **For the host's admins:** every language with its progress, adding, changing and removing a language, the
    editor's entries, saving texts, and a catalog's export and import.

  Who counts as an admin is the host's policy (`LanguagesHttpOptions.Policy`) or its `ILanguagesRights`. With neither,
  no one may change anything. A change is told to the host (`ILanguagesEvents`), for its audit and its own caches.
- **What stays the host's:**
  - names stored by language in its own data, and searching them;
  - which language a person chose (a cookie, an account setting);
  - how the web shell loads the bundle.

  The package gives the chain for a request (`LanguagesCache.ChainFor`), and the host sets it wherever its code reads it.

## Consequences

- A second product gets the languages page, the editor and the bundles by serving its catalogs and naming its admin
  policy.
- The first product keeps its tables, routes and bundle shape. It loses about a thousand lines of its own code.
- An identity package that keeps a site's list of languages in configuration can read them from here through a seam
  of its own instead, and fall back to its configuration without it.
- Server messages (exceptions' texts) aren't catalogs yet; they stay in the source language.

## Where it lives (2026-11-21)

The packages were made in a repository of their own and moved here before their first release, to keep fewer
repositories to maintain. Like AbpGateway and AbpWebhooks, they aren't about running on several servers and nothing in
them uses AbpCluster: the repository holds them, not the AbpCluster package. They keep their names (`AbpLanguages`,
`AbpLanguages.HttpApi`) and modules, so a host's code doesn't change if they move out again, and they're released with
this repository's version and tag.
