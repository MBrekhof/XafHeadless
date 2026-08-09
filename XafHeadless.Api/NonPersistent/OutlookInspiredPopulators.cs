using DevExpress.ExpressApp;
using OutlookInspiredDemo.Module.BusinessObjects;

namespace XafHeadless.Api.NonPersistent;

// NPO-001: DEMO-ONLY SCAFFOLDING. Read this before copying the pattern.
//
// A real adopting app should NOT restate its population logic here. It should extract the body of its
// existing ListView controller's ObjectsGetting handler into a plain method, and have both the controller
// and its host registration call that one method -- no duplication, one place to change.
//
// That is not available here: OutlookInspired.Module is a READ-ONLY reference (README, "the 26.1 seed" --
// it lives in the DevExpress demos install and this repo does not modify it), so its handler body cannot be
// extracted. The query shape is therefore restated below.
//
// What is deliberately NOT restated: the domain constants. Stage.Range() is public on the module's own
// StageExtensions, so the band boundaries that decide which quotes count toward which stage come from the
// module itself. The two copies cannot drift on the thing that would actually produce wrong numbers.
//
// Mirrors OutlookInspiredDemo.Module/Features/Quotes/OpportunitiesListViewController.cs.
public static class OutlookInspiredPopulators {
    public static NonPersistentRegistry AddOutlookInspiredDemo(this NonPersistentRegistry registry) =>
        // Exactly 4 rows, always: the Stage enum minus Summary. Bounded by an enum, so this one needs no
        // cap and no seeded data to be worth serving -- which is why NPO-001 names it the first subject to
        // prove the path on.
        registry.Register<Opportunity>((os, _) =>
            Enum.GetValues<Stage>()
                .Where(stage => stage != Stage.Summary)
                .Select((stage, i) => {
                    var (min, max) = stage.Range();
                    return new Opportunity {
                        ID = i,
                        Stage = stage,
                        // Aggregated by the DATABASE through the additional (secured) ObjectSpace that
                        // OnObjectSpaceCreated attaches -- the same route the demo's controller takes, so
                        // the sum is over rows this user is permitted to read. Sum() over no rows is 0,
                        // which is the correct answer for an unseeded database, not an error.
                        Value = (decimal)os.GetObjectsQuery<Quote>()
                            .Where(quote => quote.Opportunity > min && quote.Opportunity < max)
                            .Select(quote => (double)quote.Total)
                            .Sum()
                    };
                })
                .ToList())
        // One row per Quote -- the unbounded case, and the reason the endpoint caps at all. Mirrors
        // OutlookInspiredDemo.Module/Features/Quotes/QuoteAnalysisListViewController.cs, including its
        // shape: project to an anonymous type in SQL, materialize, then build the non-persistent objects.
        // The demo materializes the whole table here too -- ObjectsGettingEventArgs carries no skip/top, so
        // XAF has no way to page this and neither do we. Parity, not a compromise.
        .Register<QuoteAnalysis>((os, _) =>
            os.GetObjectsQuery<Quote>()
                .Select(quote => new {
                    quote.CustomerStore.State, quote.CustomerStore.City,
                    quote.Opportunity, quote.Total, quote.Date
                })
                .ToArray()
                .Select((t, i) => new QuoteAnalysis {
                    ID = i, State = t.State, City = t.City,
                    Date = t.Date, Total = t.Total, Opportunity = t.Opportunity
                })
                .ToList());
}
