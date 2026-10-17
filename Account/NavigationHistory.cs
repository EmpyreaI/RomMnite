using System;
using System.Collections.Generic;

namespace RomM.Account
{
    /// <summary>One place that can be gone back to: a Store page, or a library page or game.</summary>
    public sealed class NavEntry
    {
        public bool Store;          // the Store, else the library
        public string Kind;         // Store: home, category, game, grid. Library: Home, Downloads, game.
        public string Value;        // a category's name (genre, series, platform or search text)
        public string SubKind;      // a category's kind
        public int Id;              // a rom id (Store game) or platform id (Store category)
        public Guid GameId;         // a library game

        public bool SameAs(NavEntry other)
        {
            return other != null && Store == other.Store && Kind == other.Kind && Value == other.Value
                && SubKind == other.SubKind && Id == other.Id && GameId == other.GameId;
        }
    }

    /// <summary>
    /// Back and forward through the places visited, as Steam's arrows go: the pages show
    /// themselves and report where they are (<see cref="Record"/>); the arrows ask to go to an
    /// earlier or later one (<see cref="Navigate"/>), and the place itself is shown by whoever
    /// handles that. What showing it reports back is not recorded again.
    /// </summary>
    public sealed class NavigationHistory
    {
        // How many places back (and forward) are kept.
        private const int Limit = 10;

        private readonly List<NavEntry> _back = new List<NavEntry>();
        private readonly List<NavEntry> _forward = new List<NavEntry>();
        private NavEntry _current;

        // A place being gone back (or forward) to, until it reports that it is showing. Pages
        // passed on the way (the Store's Home, briefly, before a game's page) are not steps.
        private NavEntry _restoring;
        private DateTime _restoreUntil = DateTime.MinValue;

        /// <summary>Raised to show a place, when going back or forward.</summary>
        public event Action<NavEntry> Navigate;

        /// <summary>Raised when going back or forward becomes possible or not.</summary>
        public event Action Changed;

        public bool CanGoBack => _back.Count > 0;
        public bool CanGoForward => _forward.Count > 0;

        /// <summary>Where things are now. A new place clears the way forward, as in a browser.</summary>
        public void Record(NavEntry entry)
        {
            if (entry == null)
                return;

            // Going back or forward is under way: what the pages report on the way is that move,
            // not a new step. It is over once the place itself reports (or, should it never,
            // after a few seconds).
            if (_restoring != null)
            {
                if (DateTime.UtcNow < _restoreUntil && !entry.SameAs(_restoring))
                    return;
                _restoring = null;
            }
            if (entry.SameAs(_current))
                return;

            if (_current != null)
                Push(_back, _current);
            _current = entry;
            _forward.Clear();
            Changed?.Invoke();
        }

        public void Back()
        {
            if (_back.Count == 0)
                return;
            NavEntry target = _back[_back.Count - 1];
            _back.RemoveAt(_back.Count - 1);
            if (_current != null)
                Push(_forward, _current);
            Go(target);
        }

        public void Forward()
        {
            if (_forward.Count == 0)
                return;
            NavEntry target = _forward[_forward.Count - 1];
            _forward.RemoveAt(_forward.Count - 1);
            if (_current != null)
                Push(_back, _current);
            Go(target);
        }

        private static void Push(List<NavEntry> stack, NavEntry entry)
        {
            stack.Add(entry);
            if (stack.Count > Limit)
                stack.RemoveAt(0);
        }

        private void Go(NavEntry target)
        {
            _current = target;
            _restoring = target;
            _restoreUntil = DateTime.UtcNow.AddSeconds(3);
            Changed?.Invoke();
            Navigate?.Invoke(target);
        }
    }
}
