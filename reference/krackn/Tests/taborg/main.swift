import Foundation

var pass = 0, fail = 0
func out(_ t: String) { print(t) }
func ok(_ n: String) { pass += 1; out("  ok   \(n)") }
func bad(_ n: String, _ detail: String) { fail += 1; out("  FAIL \(n): \(detail)") }

func expect(_ n: String, _ got: [String], _ want: [String]) {
    if got == want { ok("\(n) -> \(got.joined(separator: ","))") }
    else { bad(n, "got [\(got.joined(separator: ","))] want [\(want.joined(separator: ","))]") }
}

func expect(_ n: String, _ got: Bool, _ want: Bool) {
    if got == want { ok("\(n) -> \(got)") } else { bad(n, "got \(got) want \(want)") }
}

// MARK: - Fixtures

let base = Date(timeIntervalSince1970: 1_700_000_000)
var ids: [String: UUID] = [:]
var names: [UUID: String] = [:]

func tab(_ name: String, domain: String, accessedMinutesAgo: Double = 0,
         createdMinutesAgo: Double = 0, pinned: Bool = false,
         active: Bool = false) -> TabSnapshot {
    let id = ids[name] ?? UUID()
    ids[name] = id
    names[id] = name
    return TabSnapshot(
        id: id, title: name, domain: domain,
        lastAccessed: base.addingTimeInterval(-accessedMinutesAgo * 60),
        createdAt: base.addingTimeInterval(-createdMinutesAgo * 60),
        isPinned: pinned, isActive: active)
}

func order(_ tabs: [TabSnapshot], _ strategy: TabOrganization) -> [String] {
    TabOrganizer.order(tabs, by: strategy).map { names[$0] ?? "?" }
}

// MARK: - Organisation

out("── organisation")

let mixed = [
    tab("gmail", domain: "google.com", createdMinutesAgo: 50),
    tab("bbc", domain: "bbc.co.uk", createdMinutesAgo: 40),
    tab("docs", domain: "google.com", createdMinutesAgo: 30),
    tab("apple", domain: "apple.com", createdMinutesAgo: 20),
    tab("drive", domain: "google.com", createdMinutesAgo: 10),
]

expect("manual leaves order alone", order(mixed, .manual),
       ["gmail", "bbc", "docs", "apple", "drive"])

// Same-site tabs clump, and sites keep first-appearance order — so opening a
// new site never shuffles the tabs already on screen.
expect("by domain clumps same site", order(mixed, .domain),
       ["gmail", "docs", "drive", "bbc", "apple"])

expect("alphabetical", order(mixed, .alphabetical),
       ["apple", "bbc", "docs", "drive", "gmail"])

// createdMinutesAgo counts backwards, so "drive" (10 min ago) is newest.
expect("newest first", order(mixed, .newest),
       ["drive", "apple", "docs", "bbc", "gmail"])

let byUse = [
    tab("a", domain: "a.com", accessedMinutesAgo: 30),
    tab("b", domain: "b.com", accessedMinutesAgo: 5),
    tab("c", domain: "c.com", accessedMinutesAgo: 60),
]
expect("recently used", order(byUse, .recentlyUsed), ["b", "a", "c"])

// Case- and diacritic-insensitive, so names sort where a person looks.
let cased = [
    tab("zebra", domain: "z.com"),
    tab("Álvaro", domain: "a.com"),
    tab("apple", domain: "b.com"),
]
expect("alphabetical ignores case and accents", order(cased, .alphabetical),
       ["Álvaro", "apple", "zebra"])

// MARK: - Pinned tabs are exempt

out("── pinning")

let withPins = [
    tab("loose1", domain: "z.com", createdMinutesAgo: 10),
    tab("pin1", domain: "m.com", createdMinutesAgo: 50, pinned: true),
    tab("pin2", domain: "n.com", createdMinutesAgo: 40, pinned: true),
    tab("loose2", domain: "a.com", createdMinutesAgo: 20),
]

// Pinned first, in their own order, whatever the strategy says. The loose
// tabs still sort normally around them — titles "loose1" < "loose2".
expect("pinned stay first under alphabetical", order(withPins, .alphabetical),
       ["pin1", "pin2", "loose1", "loose2"])
expect("pinned stay first under domain", order(withPins, .domain),
       ["pin1", "pin2", "loose1", "loose2"])
// pin1 was created *before* pin2, so "newest first" would put pin2 ahead of it
// if pinned tabs were being sorted. They keep insertion order instead.
expect("pinned are exempt from sorting", order(withPins, .newest),
       ["pin1", "pin2", "loose1", "loose2"])

// Blank new tabs bucket together rather than piling under the first real site,
// and the bucket takes its place by first appearance like any other site.
let withBlank = [
    tab("real", domain: "x.com"),
    tab("blank1", domain: ""),
    tab("other", domain: "y.com"),
    tab("blank2", domain: ""),
]
expect("empty domains form their own group", order(withBlank, .domain),
       ["real", "blank1", "blank2", "other"])

// MARK: - Auto-close

out("── auto-close")

let now = base
var policy = AutoClosePolicy(enabled: true, rule: .hour)

func closing(_ tabs: [TabSnapshot], _ p: AutoClosePolicy) -> [String] {
    TabLifecycle.tabsToClose(tabs, policy: p, now: now).map { names[$0] ?? "?" }.sorted()
}

let aged = [
    tab("fresh", domain: "a.com", accessedMinutesAgo: 5),          // 5 minutes
    tab("stale", domain: "b.com", accessedMinutesAgo: 120),        // 2 hours
    tab("ancient", domain: "c.com", accessedMinutesAgo: 20_000),   // ~14 days
]
expect("1 hour closes anything older", closing(aged, policy), ["ancient", "stale"])

policy.rule = .day
expect("1 day spares the 2-hour-old tab", closing(aged, policy), ["ancient"])

policy.rule = .week
expect("7 days spares all but the 14-day-old", closing(aged, policy), ["ancient"])

policy.rule = .custom
policy.customHours = 1.5
expect("custom 1.5h", closing(aged, policy), ["ancient", "stale"])

policy.enabled = false
expect("disabled closes nothing", closing(aged, policy), [])

// The three exemptions, each for a different reason.
policy = AutoClosePolicy(enabled: true, rule: .hour)

let withActive = [
    tab("active", domain: "a.com", accessedMinutesAgo: 5000, active: true),
    tab("other", domain: "b.com", accessedMinutesAgo: 5000),
    tab("third", domain: "c.com", accessedMinutesAgo: 5000),
]
expect("never closes the active tab", closing(withActive, policy), ["other", "third"])

let withPinned = [
    tab("pinned", domain: "a.com", accessedMinutesAgo: 5000, pinned: true),
    tab("loose", domain: "b.com", accessedMinutesAgo: 5000),
    tab("loose2", domain: "c.com", accessedMinutesAgo: 5000),
]
expect("never closes a pinned tab", closing(withPinned, policy), ["loose", "loose2"])

let onlyOne = [tab("solo", domain: "a.com", accessedMinutesAgo: 99999)]
expect("never closes the last tab", closing(onlyOne, policy), [])

let two = [
    tab("one", domain: "a.com", accessedMinutesAgo: 99999),
    tab("two", domain: "b.com", accessedMinutesAgo: 99999),
]
if closing(two, policy).count == 1 { ok("leaves one survivor from two stale tabs") }
else { bad("survivor count", "got \(closing(two, policy))") }

// The scheduled sweep ignores age but keeps the same exemptions.
let scheduled = TabLifecycle.tabsToCloseOnSchedule(withPinned).map { names[$0] ?? "?" }.sorted()
expect("scheduled sweep spares pinned", scheduled, ["loose", "loose2"])

// MARK: - Schedule crossing

out("── schedule window")

var cal = Calendar(identifier: .gregorian)
cal.timeZone = TimeZone(secondsFromGMT: 0)!

var timed = AutoClosePolicy(enabled: true, rule: .day)
timed.atTimeEnabled = true
timed.hour = 3
timed.minute = 0

func at(_ y: Int, _ m: Int, _ d: Int, _ h: Int, _ min: Int) -> Date {
    cal.date(from: DateComponents(year: y, month: m, day: d, hour: h, minute: min))!
}

expect("fires when 03:00 falls inside the window",
       TabLifecycle.scheduleElapsed(policy: timed, since: at(2026, 3, 1, 2, 55),
                                    now: at(2026, 3, 1, 3, 5), calendar: cal), true)
expect("doesn't fire before the time",
       TabLifecycle.scheduleElapsed(policy: timed, since: at(2026, 3, 1, 1, 0),
                                    now: at(2026, 3, 1, 2, 0), calendar: cal), false)
expect("doesn't fire after it already passed",
       TabLifecycle.scheduleElapsed(policy: timed, since: at(2026, 3, 1, 4, 0),
                                    now: at(2026, 3, 1, 5, 0), calendar: cal), false)
// The reason it's an interval test and not "is it 03:00 now": a machine asleep
// across the instant must still sweep when it wakes.
expect("fires after sleeping across the instant",
       TabLifecycle.scheduleElapsed(policy: timed, since: at(2026, 3, 1, 22, 0),
                                    now: at(2026, 3, 2, 9, 0), calendar: cal), true)
expect("a multi-day gap still fires exactly once",
       TabLifecycle.scheduleElapsed(policy: timed, since: at(2026, 3, 1, 4, 0),
                                    now: at(2026, 3, 5, 4, 0), calendar: cal), true)
timed.atTimeEnabled = false
expect("no schedule when the option is off",
       TabLifecycle.scheduleElapsed(policy: timed, since: at(2026, 3, 1, 2, 0),
                                    now: at(2026, 3, 1, 4, 0), calendar: cal), false)

// MARK: - Interval description

out("── interval labels")

func label(_ hours: Double) -> String {
    var p = AutoClosePolicy(enabled: true, rule: .custom)
    p.customHours = hours
    return p.describedInterval
}
func expectText(_ n: String, _ got: String, _ want: String) {
    if got == want { ok("\(n) -> \(got)") } else { bad(n, "got \(got) want \(want)") }
}
expectText("30 minutes", label(0.5), "30 minutes")
expectText("1 hour", label(1), "1 hour")
expectText("6 hours", label(6), "6 hours")
expectText("1 day", label(24), "1 day")
expectText("3 days", label(72), "3 days")

// MARK: - Stacks

out("── dropdown style: one entry per site, in the arrangement's order")
let st = TabOrganizer.stacks(mixed)
expectText("one stack per site", "\(st.count)", "3")
expectText("sites in the order they first appeared", st.map(\.domain).joined(separator: ","),
           "google.com,bbc.co.uk,apple.com")
expectText("a site's pages keep their order inside its stack",
           st[0].ids.compactMap { id in mixed.first { $0.id == id }?.title }.joined(separator: ","),
           "gmail,docs,drive")
// The arrangement decides the order: tabs arrive already arranged.
let recentFirst = TabOrganizer.order(mixed, by: .newest).compactMap { id in mixed.first { $0.id == id } }
expectText("Newest + Dropdown: the site with the newest page comes first",
           TabOrganizer.stacks(recentFirst).map(\.domain).first ?? "", "google.com")
expectText("A–Z + Dropdown: sites sort by name",
           TabOrganizer.stacks(mixed, alphabetical: true).map(\.domain).joined(separator: ","),
           "apple.com,bbc.co.uk,google.com")
expectText("A–Z keeps new tabs after the sites",
           TabOrganizer.stacks(mixed + [tab("blank", domain: "")], alphabetical: true).last?.domain ?? "x", "")
let stackBlank = mixed + [tab("new1", domain: ""), tab("new2", domain: "")]
expectText("blank new tabs are each their own stack", "\(TabOrganizer.stacks(stackBlank).count)", "5")
let stackPinned = [tab("pin", domain: "google.com", pinned: true)] + mixed
expectText("pinned tabs aren't stacked",
           "\(TabOrganizer.stacks(stackPinned)[0].ids.count)", "3")

// MARK: - Domain grouping helper

out("── registrable domain")

func expectDomain(_ host: String, _ want: String) {
    let got = ThreatHeuristics.registrableDomain(host)
    if got == want { ok("\(host) -> \(got)") } else { bad(host, "got \(got) want \(want)") }
}
expectDomain("docs.google.com", "google.com")
expectDomain("google.com", "google.com")
expectDomain("a.b.c.example.com", "example.com")
// Two-part public suffixes must not collapse to the meaningless "co.uk".
expectDomain("news.bbc.co.uk", "bbc.co.uk")
expectDomain("bbc.co.uk", "bbc.co.uk")
expectDomain("shop.example.com.au", "example.com.au")
expectDomain("localhost", "localhost")
// Root domain regardless of how the host is written.
expectDomain("MAIL.Google.COM", "google.com")
expectDomain("www.youtube.com.", "youtube.com")
expectDomain("m.youtube.com", "youtube.com")
expectDomain("en.wikipedia.org", "wikipedia.org")
expectDomain("forums.example.co.jp", "example.co.jp")
expectDomain("www.example.com.hk", "example.com.hk")
// Addresses are not domains: cutting them would merge different machines.
expectDomain("192.168.1.10", "192.168.1.10")
expectDomain("10.0.0.1", "10.0.0.1")
expectDomain("::1", "::1")

out("── Dropdown style: Google's products are separate dropdowns")
do {
    func group(_ url: String) -> SiteGroup.Group {
        let u = URL(string: url)!
        let host = u.host ?? ""
        return SiteGroup.of(host: host, path: u.path, domain: ThreatHeuristics.registrableDomain(host))
    }
    func named(_ url: String, _ want: String) { expect("\(url) → \(want)", [group(url).name], [want]) }
    named("https://docs.google.com/document/d/abc/edit", "Google Docs")
    named("https://docs.google.com/document/u/0/", "Google Docs")
    named("https://docs.google.com/spreadsheets/d/abc/edit", "Google Sheets")
    named("https://docs.google.com/presentation/d/abc/edit", "Google Slides")
    named("https://docs.google.com/forms/d/abc/viewform", "Google Forms")
    named("https://gemini.google.com/app/123", "Gemini")
    named("https://mail.google.com/mail/u/0/#inbox", "Gmail")
    named("https://drive.google.com/drive/my-drive", "Google Drive")
    named("https://www.google.com/search?q=krackn", "Google")
    named("https://www.google.com/url?q=https://example.com", "Google")
    named("https://www.google.com/maps/place/x", "Google Maps")
    named("https://www.google.co.uk/search?q=x", "Google")
    named("https://accounts.google.com/signin", "Google Account")
    named("https://unknownthing.google.com/", "unknownthing.google.com")
    // Everything else is still one dropdown per site.
    named("https://en.wikipedia.org/wiki/Kraken", "wikipedia.org")
    named("https://github.com/apple/swift", "github.com")
    named("https://www.youtube.com/watch?v=1", "youtube.com")
    // Look-alikes aren't Google.
    named("https://google.xyz/", "google.xyz")
    named("https://google.com.evil.com/", "evil.com")
    expect("two Docs share a dropdown",
           group("https://docs.google.com/document/d/a").key == group("https://docs.google.com/document/d/b").key, true)
    expect("a Doc and a Sheet don't",
           group("https://docs.google.com/document/d/a").key == group("https://docs.google.com/spreadsheets/d/a").key, false)
    expect("two Wikipedia languages still share one",
           group("https://en.wikipedia.org/").key == group("https://de.wikipedia.org/").key, true)

    func snap(_ url: String) -> TabSnapshot {
        let g = group(url)
        let host = URL(string: url)!.host!
        return TabSnapshot(id: UUID(), title: url, domain: ThreatHeuristics.registrableDomain(host),
                           group: g.key, groupName: g.name, lastAccessed: Date(), createdAt: Date())
    }
    let mixed = ["https://docs.google.com/document/d/1", "https://gemini.google.com/app",
                 "https://docs.google.com/presentation/d/1", "https://docs.google.com/document/d/2",
                 "https://www.google.com/search?q=a", "https://github.com/x"].map(snap)
    expect("stacks: Docs ×2, Gemini, Slides, Google, GitHub",
           TabOrganizer.stacks(mixed).map { "\($0.name) \($0.ids.count)" },
           ["Google Docs 2", "Gemini 1", "Google Slides 1", "Google 1", "github.com 1"])
    expect("A–Z sorts by the name shown",
           TabOrganizer.stacks(mixed, alphabetical: true).map(\.name),
           ["Gemini", "github.com", "Google", "Google Docs", "Google Slides"])
}

out("── Dropdown style: reordering stacks and the pages inside one")
do {
    let ids = (0..<6).map { _ in UUID() }
    let name = Dictionary(uniqueKeysWithValues: zip(ids, ["a1", "a2", "b1", "c1", "c2", "c3"]))
    let stacks = [[ids[0], ids[1]], [ids[2]], [ids[3], ids[4], ids[5]]]
    func flat(_ s: [[UUID]]) -> [String] { s.flatMap { $0 }.map { name[$0]! } }
    expect("stack dragged right takes all its pages along",
           flat(TabOrganizer.movingStack(stacks, from: 0, to: 2)), ["b1", "c1", "c2", "c3", "a1", "a2"])
    expect("stack dragged left",
           flat(TabOrganizer.movingStack(stacks, from: 2, to: 0)), ["c1", "c2", "c3", "a1", "a2", "b1"])
    expect("out-of-range move changes nothing",
           flat(TabOrganizer.movingStack(stacks, from: 0, to: 9)), ["a1", "a2", "b1", "c1", "c2", "c3"])
    let c = [ids[3], ids[4], ids[5]]
    expect("page dragged to the bottom of its list",
           TabOrganizer.moving(ids[3], in: c, to: 2).map { name[$0]! }, ["c2", "c3", "c1"])
    expect("page dragged to the top",
           TabOrganizer.moving(ids[5], in: c, to: 0).map { name[$0]! }, ["c3", "c1", "c2"])
    expect("past the end clamps to the end",
           TabOrganizer.moving(ids[3], in: c, to: 99).map { name[$0]! }, ["c2", "c3", "c1"])
    expect("an id not in the list changes nothing",
           TabOrganizer.moving(ids[0], in: c, to: 0).map { name[$0]! }, ["c1", "c2", "c3"])
}

out("")
out("\(pass) passed, \(fail) failed")
exit(fail == 0 ? 0 : 1)
