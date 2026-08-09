using System.Collections;
using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;

namespace XafHeadless.Api.NonPersistent;

// NPO-001: the host-side seam that gives a non-persistent [DomainComponent] type a wire representation.
//
// Why this exists at all. XAF apps model computed/aggregate screens as non-persistent types with no
// DbSet, populated in memory. Data otherwise reaches this client over OData, and options.BusinessObject<T>()
// exposes EF entities -- a type with no table cannot be queried that way, so such a view projected its
// metadata fine and then failed to load any data.
//
// Why a registry HERE rather than reusing the app's own population code where it already lives. XAF apps
// populate these types from an ObjectViewController<ListView, T>, which activates only inside a Frame with a
// live View -- neither exists in a headless host. The population logic itself is UI-free (plain LINQ over
// IObjectSpace); only the SUBSCRIPTION is UI-bound. So the fix is to subscribe it somewhere headless code
// reaches: NonPersistentObjectSpace.ObjectsGetting, wired once in Startup.
//
// NPO-001 weighed three seams and chose this one (option B). Making the app move its own subscription out of
// its controller (option A) is a smaller change here and a worse one for consumers -- it edits a module that
// is already running in production. This keeps the change confined to headless-host startup, which an
// adopting app is writing anyway to adopt this platform at all; its existing Blazor app and controllers are
// untouched.
//
// Deliberately NOT a re-implementation of XAF's UI stack: standing up a server-side Frame + ListView so the
// app's controllers activate was considered and rejected. The goal is parity of OUTCOME, not of mechanism.

// criteria is the collection source's filter, forwarded from ObjectsGettingEventArgs.Criteria. A populator
// may honour it or ignore it -- an aggregate over a whole table (Opportunity) has nothing to filter on.
public delegate IList PopulateNonPersistent(IObjectSpace objectSpace, CriteriaOperator? criteria);

public sealed class NonPersistentRegistry {
    readonly Dictionary<Type, PopulateNonPersistent> populators = [];

    public NonPersistentRegistry Register<T>(PopulateNonPersistent populate) {
        populators[typeof(T)] = populate;
        return this;
    }

    public bool IsRegistered(Type type) => populators.ContainsKey(type);
    public PopulateNonPersistent? Find(Type type) => populators.GetValueOrDefault(type);

    // Subscribes a freshly created NonPersistentObjectSpace to this registry. Called from the host's
    // ObjectSpaceProviders.Events.OnObjectSpaceCreated -- the documented seam for exactly this
    // (dxdocs 403164, which names MySolution.WebApi/Startup.cs).
    //
    // An unregistered type is left alone rather than rejected: NonPersistentObjectSpace.CreateCollection
    // falls through to an empty BindingList when nothing subscribes, so a type this host does not serve
    // behaves exactly as it did before.
    public void Attach(NonPersistentObjectSpace objectSpace) {
        objectSpace.ObjectsGetting += (sender, e) => {
            var populate = Find(e.ObjectType);
            if (populate is null) return;
            e.Objects = populate((IObjectSpace)sender!, e.Criteria);
        };
        // Without ObjectByKeyGetting, opening a DetailView on one of these rows throws error 1021
        // ("object belongs to another ObjectSpace") -- the trap the xaf-blazor-startup skill flags. These
        // objects are computed fresh per request and have no store to re-read, so resolving a key means
        // re-populating and matching on it.
        objectSpace.ObjectByKeyGetting += (sender, e) => {
            var populate = Find(e.ObjectType);
            if (populate is null) return;
            var os = (IObjectSpace)sender!;
            e.Object = populate(os, null).Cast<object>()
                .FirstOrDefault(o => Equals(os.GetKeyValue(o), e.Key));
        };
    }
}
