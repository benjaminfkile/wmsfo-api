using System.Text.Json.Nodes;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts;

// Builds the seed ContentDocument (contracts 1.3a). One source used by:
//   - the fixture writer (`contracts/fixtures/content-document.json`)
//   - the seed file (`contracts/starter-content.json`)
//   - the snapshot fixture's `content`
// Every icon references the built-in library; no media assets are used so a fresh
// bucket publishes cleanly. All sections satisfy the publish-level schemas.
public static class StarterContentBuilder
{
    public static ContentDocument Build()
    {
        var doc = new ContentDocument
        {
            SchemaVersion = 1,
            Settings = BuildSettings(),
            Pages = BuildPages(),
        };
        return doc;
    }

    private static SiteSettings BuildSettings() => new()
    {
        SiteName = "Western Montana Santa Flyover",
        Tagline = "Santa flies over the valley every December.",
        HomeNavLabel = "Track Santa",
        Logo = new IconValue { Source = "library", Id = "sleigh" },
        Favicon = new IconValue { Source = "library", Id = "santa-hat" },
        Theme = new SiteTheme
        {
            Accent = "red",
            Surface = "snow",
            FontPairing = "festive",
            SnowDefault = true,
        },
        NavExtraLinks = new List<LinkValue>(),
        FooterLinks = new List<LinkValue>(),
        FooterText = "A volunteer project.",
        ContactEmail = null,
        DonateUrl = null,
        AnalyticsEnabled = false,
    };

    private static IList<ContentPage> BuildPages()
    {
        // Section ids are just stable integers within the document. Pages get ids too.
        // Contract order: role pages first in status order (no_event first), then `none` by navPosition.
        long pageId = 1;
        long sectionId = 1;
        var pages = new List<ContentPage>
        {
            BuildRolePage(pageId++, "no-event",  "Off season",     "no_event",  ref sectionId, WithoutCountdown),
            BuildRolePage(pageId++, "planned",   "Coming this December", "planned", ref sectionId, WithoutCountdown),
            BuildRolePage(pageId++, "scheduled", "Santa is on the way",  "scheduled", ref sectionId, ScheduledStack),
            BuildRolePage(pageId++, "live",      "Santa is airborne",    "live",     ref sectionId, LiveStack),
            BuildRolePage(pageId++, "ended",     "Merry Christmas",      "ended",    ref sectionId, EndedStack),
            BuildRolePage(pageId++, "cancelled", "Flight cancelled",     "cancelled", ref sectionId, WithoutCountdown),

            BuildOrdinaryPage(pageId++, "about",    "About the flyover", "About",    10, ref sectionId, AboutStack),
            BuildOrdinaryPage(pageId++, "sponsors", "Sponsors",          "Sponsors", 20, ref sectionId, SponsorsStack),
            BuildOrdinaryPage(pageId++, "route",    "The route",         "Route",    30, ref sectionId, RouteStack),
            BuildOrdinaryPage(pageId++, "donate",   "Support the flyover", "Donate", 40, ref sectionId, DonateStack),
            BuildOrdinaryPage(pageId++, "contact",  "Contact us",        "Contact",  50, ref sectionId, ContactStack),
            BuildOrdinaryPage(pageId++, "alerts",   "Get alerts",        "Alerts",   60, ref sectionId, AlertsStack),
        };
        return pages;
    }

    private static ContentPage BuildRolePage(
        long id, string slug, string title, string role,
        ref long sectionId, Action<List<ContentSection>, RefLong> stackBuilder)
    {
        var sections = new List<ContentSection>();
        var refId = new RefLong { Value = sectionId };
        stackBuilder(sections, refId);
        sectionId = refId.Value;
        return new ContentPage
        {
            Id = id,
            Slug = slug,
            Title = title,
            NavLabel = null,
            NavPosition = 0,
            Role = role,
            Sections = sections,
        };
    }

    private static ContentPage BuildOrdinaryPage(
        long id, string slug, string title, string navLabel, int navPosition,
        ref long sectionId, Action<List<ContentSection>, RefLong> stackBuilder)
    {
        var sections = new List<ContentSection>();
        var refId = new RefLong { Value = sectionId };
        stackBuilder(sections, refId);
        sectionId = refId.Value;
        return new ContentPage
        {
            Id = id,
            Slug = slug,
            Title = title,
            NavLabel = navLabel,
            NavPosition = navPosition,
            Role = "none",
            Sections = sections,
        };
    }

    // The section stacks per the task record and the site's story.

    private static void WithoutCountdown(List<ContentSection> s, RefLong id)
    {
        s.Add(Hero(id.Next(), "tall", "{event:name}", "Watch this space for Santa's next flight."));
        s.Add(LatestMessage(id.Next(), "card"));
        s.Add(FundsRing(id.Next()));
        s.Add(RoutePreview(id.Next(), "svg"));
        s.Add(SponsorCarousel(id.Next()));
        s.Add(AlertsSignup(id.Next()));
    }

    private static void ScheduledStack(List<ContentSection> s, RefLong id)
    {
        s.Add(Hero(id.Next(), "tall", "{event:name}", "Liftoff is scheduled for {event:scheduledAt}."));
        s.Add(Countdown(id.Next()));
        s.Add(LatestMessage(id.Next(), "card"));
        s.Add(FundsRing(id.Next()));
        s.Add(RoutePreview(id.Next(), "svg"));
        s.Add(SponsorCarousel(id.Next()));
        s.Add(AlertsSignup(id.Next()));
    }

    private static void LiveStack(List<ContentSection> s, RefLong id)
    {
        s.Add(MapSection(id.Next()));
        s.Add(Leaderboard(id.Next(), "panel"));
    }

    private static void EndedStack(List<ContentSection> s, RefLong id)
    {
        s.Add(Hero(id.Next(), "short", "Merry Christmas!", "Thanks for flying with us this year."));
        s.Add(EventTimes(id.Next()));
        s.Add(Leaderboard(id.Next(), "full"));
        s.Add(LatestMessage(id.Next(), "card"));
        s.Add(SponsorGrid(id.Next()));
    }

    private static void AboutStack(List<ContentSection> s, RefLong id)
    {
        s.Add(RichText(id.Next(), new JsonArray
        {
            Block("heading", new JsonObject { ["level"] = 2, ["text"] = "About the flyover", ["icon"] = null }),
            Block("paragraph", new JsonObject { ["text"] = "A volunteer helicopter crew flies over the Bitterroot Valley every December so kids can wave at Santa. This is the tracker." }),
            Block("paragraph", new JsonObject { ["text"] = "The crew flies at their own expense. Sponsors keep the fuel tank full." }),
        }));
    }

    private static void SponsorsStack(List<ContentSection> s, RefLong id)
    {
        s.Add(RichText(id.Next(), new JsonArray
        {
            Block("heading", new JsonObject { ["level"] = 2, ["text"] = "Sponsors", ["icon"] = null }),
            Block("paragraph", new JsonObject { ["text"] = "Every dollar keeps the sleigh in the air." }),
        }));
        s.Add(SponsorGrid(id.Next()));
    }

    private static void RouteStack(List<ContentSection> s, RefLong id)
    {
        s.Add(RichText(id.Next(), new JsonArray
        {
            Block("heading", new JsonObject { ["level"] = 2, ["text"] = "This year's route", ["icon"] = null }),
            Block("paragraph", new JsonObject { ["text"] = "Here is the planned path. Weather may adjust the details on the day." }),
        }));
        s.Add(RoutePreview(id.Next(), "svg"));
    }

    private static void DonateStack(List<ContentSection> s, RefLong id)
    {
        s.Add(RichText(id.Next(), new JsonArray
        {
            Block("heading", new JsonObject { ["level"] = 2, ["text"] = "Support the flyover", ["icon"] = null }),
            Block("paragraph", new JsonObject { ["text"] = "Fuel, insurance, and a warm hangar. Every gift helps." }),
        }));
    }

    private static void ContactStack(List<ContentSection> s, RefLong id)
    {
        s.Add(RichText(id.Next(), new JsonArray
        {
            Block("heading", new JsonObject { ["level"] = 2, ["text"] = "Contact us", ["icon"] = null }),
            Block("paragraph", new JsonObject { ["text"] = "Send a note and we will get back to you." }),
        }));
        s.Add(ContactForm(id.Next()));
    }

    private static void AlertsStack(List<ContentSection> s, RefLong id)
    {
        s.Add(RichText(id.Next(), new JsonArray
        {
            Block("heading", new JsonObject { ["level"] = 2, ["text"] = "Never miss a flight", ["icon"] = null }),
            Block("paragraph", new JsonObject { ["text"] = "Sign in and we will email you when Santa is scheduled and when he is airborne." }),
        }));
        s.Add(AlertsSignup(id.Next()));
    }

    // Section builders.
    private static ContentSection Hero(long id, string height, string title, string tagline) => new()
    {
        Id = id,
        Kind = "hero",
        Presentation = new Presentation { Width = "wide", Align = "center", Background = Presentation.BackgroundNone(), Spacing = "normal", IconBefore = null, IconAfter = null, Anchor = null },
        Data = new JsonObject
        {
            ["title"] = title,
            ["tagline"] = tagline,
            ["icon"] = new JsonObject { ["source"] = "library", ["id"] = "helicopter" },
            ["links"] = new JsonArray(),
            ["height"] = height,
        },
    };

    private static ContentSection Countdown(long id) => new()
    {
        Id = id,
        Kind = "countdown",
        Presentation = DefaultPresentation(),
        Data = new JsonObject { ["heading"] = "Countdown to liftoff" },
    };

    private static ContentSection LatestMessage(long id, string style) => new()
    {
        Id = id,
        Kind = "latest_message",
        Presentation = DefaultPresentation(),
        Data = new JsonObject { ["heading"] = "Latest update", ["style"] = style },
    };

    private static ContentSection FundsRing(long id) => new()
    {
        Id = id,
        Kind = "funds_ring",
        Presentation = DefaultPresentation(),
        Data = new JsonObject
        {
            ["heading"] = "Cheer meter",
            ["caption"] = "How the fundraiser is doing.",
            ["size"] = "large",
            ["showYear"] = true,
        },
    };

    private static ContentSection RoutePreview(long id, string style) => new()
    {
        Id = id,
        Kind = "route_preview",
        Presentation = DefaultPresentation(),
        Data = new JsonObject
        {
            ["heading"] = "This year's route",
            ["style"] = style,
            ["emptyText"] = "The route will appear here once it is set.",
        },
    };

    private static ContentSection SponsorCarousel(long id) => new()
    {
        Id = id,
        Kind = "sponsor_carousel",
        Presentation = DefaultPresentation(),
        Data = new JsonObject { ["heading"] = "Thanks to our sponsors", ["logoWidth"] = 480 },
    };

    private static ContentSection SponsorGrid(long id) => new()
    {
        Id = id,
        Kind = "sponsor_grid",
        Presentation = DefaultPresentation(),
        Data = new JsonObject
        {
            ["heading"] = "Thanks to our sponsors",
            ["columns"] = 3,
            ["showYears"] = true,
            ["emptyText"] = "Sponsors welcome.",
        },
    };

    private static ContentSection AlertsSignup(long id) => new()
    {
        Id = id,
        Kind = "alerts_signup",
        Presentation = DefaultPresentation(),
        Data = new JsonObject
        {
            ["heading"] = "Get alerts",
            ["copy"] = "We will email you when Santa is scheduled and when he lifts off.",
            ["signedOutCopy"] = "Sign in to sign up for alerts.",
        },
    };

    private static ContentSection Leaderboard(long id, string variant) => new()
    {
        Id = id,
        Kind = "leaderboard",
        Presentation = DefaultPresentation(),
        Data = new JsonObject
        {
            ["heading"] = "Cookie leaderboard",
            ["variant"] = variant,
            ["emptyText"] = "Be the first to leave a cookie.",
        },
    };

    private static ContentSection EventTimes(long id) => new()
    {
        Id = id,
        Kind = "event_times",
        Presentation = DefaultPresentation(),
        Data = new JsonObject
        {
            ["fields"] = new JsonArray { "scheduledAt", "wentLiveAt", "endedAt", "airborneFor" },
            ["labels"] = new JsonObject
            {
                ["scheduledAt"] = "Scheduled",
                ["wentLiveAt"] = "Liftoff",
                ["endedAt"] = "Wheels down",
                ["airborneFor"] = "Airborne for",
            },
        },
    };

    private static ContentSection MapSection(long id) => new()
    {
        Id = id,
        Kind = "map",
        Presentation = new Presentation { Width = "full", Align = "center", Background = Presentation.BackgroundNone(), Spacing = "normal", IconBefore = null, IconAfter = null, Anchor = null },
        Data = new JsonObject
        {
            ["themes"] = new JsonArray { "standard", "expedition", "blizzard", "charcoal", "night", "nebula" },
            ["defaultTheme"] = "night",
            ["defaultCenter"] = new JsonObject { ["lat"] = 46.87, ["lng"] = -114.0 },
            ["defaultZoom"] = 11,
            ["controls"] = new JsonObject
            {
                ["themePicker"] = true,
                ["terrain"] = true,
                ["snow"] = true,
                ["routeLines"] = true,
                ["timeLabels"] = true,
                ["location"] = true,
                ["dataRow"] = true,
            },
            ["overlays"] = new JsonObject
            {
                ["liveIndicator"] = true,
                ["liftoffTimer"] = true,
                ["latestMessage"] = true,
                ["leaderboardPanel"] = true,
                ["sponsorCarousel"] = true,
                ["cookieControl"] = true,
                ["distanceChip"] = true,
            },
        },
    };

    private static ContentSection ContactForm(long id) => new()
    {
        Id = id,
        Kind = "contact_form",
        Presentation = DefaultPresentation(),
        Data = new JsonObject
        {
            ["heading"] = "Send a note",
            ["copy"] = "We will reply as soon as we can.",
            ["successText"] = "Thanks. We will get back to you.",
        },
    };

    private static ContentSection RichText(long id, JsonArray blocks) => new()
    {
        Id = id,
        Kind = "rich_text",
        Presentation = new Presentation { Width = "narrow", Align = "start", Background = Presentation.BackgroundNone(), Spacing = "normal", IconBefore = null, IconAfter = null, Anchor = null },
        Data = new JsonObject { ["blocks"] = blocks },
    };

    private static Presentation DefaultPresentation() =>
        new()
        {
            Width = "wide",
            Align = "start",
            Background = Presentation.BackgroundNone(),
            Spacing = "normal",
            IconBefore = null,
            IconAfter = null,
            Anchor = null,
        };

    private static JsonNode Block(string kind, JsonObject rest)
    {
        var block = new JsonObject { ["kind"] = kind };
        foreach (var kv in rest)
            block[kv.Key] = kv.Value?.DeepClone();
        return block;
    }

    private sealed class RefLong
    {
        public long Value;
        public long Next() => Value++;
    }
}
