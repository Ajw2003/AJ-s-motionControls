using System;

namespace Managers
{
    /// <summary>
    /// One entry in <see cref="MatchSettings.All"/>. Holds no state of its own — every concrete
    /// setting wraps a get/set pair pointing at a property that already lives somewhere else
    /// (<see cref="RockSelection"/>, <see cref="TurnManager"/>), so there is exactly one source
    /// of truth per value and the settings list is just a generic way to browse and edit it.
    /// (Exception: share internet has no live owner, so <see cref="MatchSettings"/> holds it.)
    /// </summary>
    public abstract class Setting
    {
        public string Key { get; }
        public string Label { get; }
        public string Help { get; }

        protected Setting(string key, string label, string help)
        {
            Key = key;
            Label = label;
            Help = help;
        }

        public abstract string Display { get; }
        public abstract void Next();
        public abstract void Prev();
    }

    public class ToggleSetting : Setting
    {
        private readonly Func<bool> _get;
        private readonly Action<bool> _set;

        public ToggleSetting(string key, string label, string help, Func<bool> get, Action<bool> set)
            : base(key, label, help)
        {
            _get = get;
            _set = set;
        }

        public override string Display => _get() ? "On" : "Off";
        public override void Next() => _set(!_get());
        public override void Prev() => _set(!_get());
    }

    public class EnumSetting<T> : Setting where T : struct, Enum
    {
        private static readonly T[] Values = (T[])Enum.GetValues(typeof(T));

        private readonly Func<T> _get;
        private readonly Action<T> _set;

        public EnumSetting(string key, string label, string help, Func<T> get, Action<T> set)
            : base(key, label, help)
        {
            _get = get;
            _set = set;
        }

        public override string Display => _get().ToString();

        public override void Next() => _set(Values[(IndexOfCurrent() + 1) % Values.Length]);
        public override void Prev() => _set(Values[(IndexOfCurrent() - 1 + Values.Length) % Values.Length]);

        private int IndexOfCurrent()
        {
            var index = Array.IndexOf(Values, _get());
            return index < 0 ? 0 : index;
        }
    }

    public class IntSetting : Setting
    {
        private readonly Func<int> _get;
        private readonly Action<int> _set;
        private readonly int _min, _max, _step;

        public IntSetting(string key, string label, string help, Func<int> get, Action<int> set,
            int min, int max, int step = 1)
            : base(key, label, help)
        {
            _get = get;
            _set = set;
            _min = min;
            _max = max;
            _step = step;
        }

        public override string Display => _get().ToString();
        public override void Next() => _set(Math.Min(_max, _get() + _step));
        public override void Prev() => _set(Math.Max(_min, _get() - _step));
    }
}
