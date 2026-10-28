#nullable enable annotations
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NoteMotionControl
{
    public enum ApplicationPolicy
    {
        OnlyReduce = 0,
        Strict = 1,
    }

    public enum NjsMode
    {
        Map = 0,
        Override = 1,
        Multiplier = 2,
    }

    public enum ReductionMode
    {
        Off = 0,
        Density = 1,
        Percentage = 2,
    }

    public enum ReactionTimeMode
    {
        Game = 0,
        Specified = 1,
    }

    /// <summary>
    /// The one global Note Training Control profile. Persistence is owned by
    /// SettingsStore; this type contains only the flat settings contract.
    /// </summary>
    [JsonConverter(typeof(OptionsJsonConverter))]
    public sealed class Options
    {
        public bool Enabled { get; set; } = false;
        public ApplicationPolicy ApplicationPolicy { get; set; } = ApplicationPolicy.OnlyReduce;
        public NjsMode NjsMode { get; set; } = NjsMode.Map;
        public float OverrideNjs { get; set; } = 12f;
        public float SpeedMultiplier { get; set; } = 1f;
        public ReductionMode ReductionMode { get; set; } = ReductionMode.Off;
        public float MaxNps { get; set; } = 4f;
        public float SameHandMinIntervalMs { get; set; } = 250f;
        public float WindowSeconds { get; set; } = 1f;
        public float RetainPercent { get; set; } = 100f;
        public ReactionTimeMode ReactionTimeMode { get; set; } = ReactionTimeMode.Game;
        public float ReactionTimeMs { get; set; } = 800f;
        public bool HidePreJump { get; set; } = false;
        public bool VerticalJump { get; set; } = true;
        public bool HorizontalSwap { get; set; } = true;
        public bool SpawnRotation { get; set; } = true;
        public bool RotateTowardsPlayer { get; set; } = true;

        /// <summary>
        /// Validates values that can cross the settings/runtime boundary.
        /// UI sliders enforce their increments; runtime validation enforces
        /// finite values and the same inclusive bounds.
        /// </summary>
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(ApplicationPolicy), ApplicationPolicy))
                throw new ArgumentOutOfRangeException(nameof(ApplicationPolicy));
            if (!Enum.IsDefined(typeof(NjsMode), NjsMode))
                throw new ArgumentOutOfRangeException(nameof(NjsMode));
            if (!Enum.IsDefined(typeof(ReductionMode), ReductionMode))
                throw new ArgumentOutOfRangeException(nameof(ReductionMode));
            if (!Enum.IsDefined(typeof(ReactionTimeMode), ReactionTimeMode))
                throw new ArgumentOutOfRangeException(nameof(ReactionTimeMode));

            RequireRange(OverrideNjs, 0.1f, 100f, nameof(OverrideNjs));
            RequireRange(SpeedMultiplier, 0.1f, 5f, nameof(SpeedMultiplier));
            RequireRange(MaxNps, 0.5f, 12f, nameof(MaxNps));
            RequireRange(SameHandMinIntervalMs, 0f, 1000f, nameof(SameHandMinIntervalMs));
            RequireRange(WindowSeconds, 0.05f, 3f, nameof(WindowSeconds));
            RequireRange(RetainPercent, 5f, 100f, nameof(RetainPercent));
            RequireRange(ReactionTimeMs, 200f, 1500f, nameof(ReactionTimeMs));
        }

        public Options Copy()
        {
            return new Options
            {
                Enabled = Enabled,
                ApplicationPolicy = ApplicationPolicy,
                NjsMode = NjsMode,
                OverrideNjs = OverrideNjs,
                SpeedMultiplier = SpeedMultiplier,
                ReductionMode = ReductionMode,
                MaxNps = MaxNps,
                SameHandMinIntervalMs = SameHandMinIntervalMs,
                WindowSeconds = WindowSeconds,
                RetainPercent = RetainPercent,
                ReactionTimeMode = ReactionTimeMode,
                ReactionTimeMs = ReactionTimeMs,
                HidePreJump = HidePreJump,
                VerticalJump = VerticalJump,
                HorizontalSwap = HorizontalSwap,
                SpawnRotation = SpawnRotation,
                RotateTowardsPlayer = RotateTowardsPlayer,
            };
        }

        internal static void RequireRange(float value, float minimum, float maximum, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name);
        }
    }

    /// <summary>
    /// Json.NET normally converts strings and numbers across primitive types.
    /// The settings file is a runtime boundary, so keep it type-exact and
    /// reject unknown members, invalid enums, duplicate names, and nonfinite
    /// numeric values before an Options instance is made visible.
    /// </summary>
    internal sealed class OptionsJsonConverter : JsonConverter
    {
        private static readonly string[] Names =
        {
            "Enabled",
            "ApplicationPolicy",
            "NjsMode",
            "OverrideNjs",
            "SpeedMultiplier",
            "ReductionMode",
            "MaxNps",
            "SameHandMinIntervalMs",
            "WindowSeconds",
            "RetainPercent",
            "ReactionTimeMode",
            "ReactionTimeMs",
            "HidePreJump",
            "VerticalJump",
            "HorizontalSwap",
            "SpawnRotation",
            "RotateTowardsPlayer",
        };

        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Options);
        }

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                throw new JsonSerializationException("Options cannot be null");

            JObject profile;
            try
            {
                profile = JObject.Load(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                });
            }
            catch (JsonException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new JsonSerializationException("Invalid options object", error);
            }

            ValidateCurrentProfile(profile);
            Options options = new Options();
            ReadKnown(profile, "Enabled", token => options.Enabled = ReadBoolean(token));
            ReadKnown(profile, "ApplicationPolicy", token => options.ApplicationPolicy = (ApplicationPolicy)ReadEnum(token, typeof(ApplicationPolicy), "ApplicationPolicy"));
            ReadKnown(profile, "NjsMode", token => options.NjsMode = (NjsMode)ReadEnum(token, typeof(NjsMode), "NjsMode"));
            ReadKnown(profile, "OverrideNjs", token => options.OverrideNjs = ReadNumber(token, "OverrideNjs"));
            ReadKnown(profile, "SpeedMultiplier", token => options.SpeedMultiplier = ReadNumber(token, "SpeedMultiplier"));
            ReadKnown(profile, "ReductionMode", token => options.ReductionMode = (ReductionMode)ReadEnum(token, typeof(ReductionMode), "ReductionMode"));
            ReadKnown(profile, "MaxNps", token => options.MaxNps = ReadNumber(token, "MaxNps"));
            ReadKnown(profile, "SameHandMinIntervalMs", token => options.SameHandMinIntervalMs = ReadNumber(token, "SameHandMinIntervalMs"));
            ReadKnown(profile, "WindowSeconds", token => options.WindowSeconds = ReadNumber(token, "WindowSeconds"));
            ReadKnown(profile, "RetainPercent", token => options.RetainPercent = ReadNumber(token, "RetainPercent"));
            ReadKnown(profile, "ReactionTimeMode", token => options.ReactionTimeMode = (ReactionTimeMode)ReadEnum(token, typeof(ReactionTimeMode), "ReactionTimeMode"));
            ReadKnown(profile, "ReactionTimeMs", token => options.ReactionTimeMs = ReadNumber(token, "ReactionTimeMs"));
            ReadKnown(profile, "HidePreJump", token => options.HidePreJump = ReadBoolean(token));
            ReadKnown(profile, "VerticalJump", token => options.VerticalJump = ReadBoolean(token));
            ReadKnown(profile, "HorizontalSwap", token => options.HorizontalSwap = ReadBoolean(token));
            ReadKnown(profile, "SpawnRotation", token => options.SpawnRotation = ReadBoolean(token));
            ReadKnown(profile, "RotateTowardsPlayer", token => options.RotateTowardsPlayer = ReadBoolean(token));
            options.Validate();
            return options;
        }

        internal static void ValidateCurrentProfile(JObject profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            foreach (JProperty property in profile.Properties())
            {
                if (Array.IndexOf(Names, property.Name) < 0)
                    throw new JsonSerializationException("Unknown setting: " + property.Name);
                switch (property.Name)
                {
                    case "Enabled":
                    case "HidePreJump":
                    case "VerticalJump":
                    case "HorizontalSwap":
                    case "SpawnRotation":
                    case "RotateTowardsPlayer":
                        RequireType(property, JTokenType.Boolean);
                        break;
                    case "ApplicationPolicy":
                        RequireEnum(property, typeof(ApplicationPolicy));
                        break;
                    case "NjsMode":
                        RequireEnum(property, typeof(NjsMode));
                        break;
                    case "ReductionMode":
                        RequireEnum(property, typeof(ReductionMode));
                        break;
                    case "ReactionTimeMode":
                        RequireEnum(property, typeof(ReactionTimeMode));
                        break;
                    default:
                        RequireNumber(property);
                        break;
                }
            }
        }

        internal static void RequireType(JProperty property, JTokenType type)
        {
            if (property == null || property.Value == null || property.Value.Type != type)
                throw new JsonSerializationException("Invalid setting type: " + (property == null ? string.Empty : property.Name));
        }

        internal static void RequireNumber(JProperty property)
        {
            if (property == null || property.Value == null ||
                (property.Value.Type != JTokenType.Integer && property.Value.Type != JTokenType.Float))
                throw new JsonSerializationException("Invalid setting type: " + (property == null ? string.Empty : property.Name));
            try
            {
                float value = property.Value.Value<float>();
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new JsonSerializationException("Invalid setting number: " + property.Name);
            }
            catch (JsonSerializationException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new JsonSerializationException("Invalid setting number: " + property.Name, error);
            }
        }

        internal static void RequireEnum(JProperty property, Type enumType)
        {
            RequireType(property, JTokenType.Integer);
            int value;
            try { value = property.Value.Value<int>(); }
            catch (Exception error) { throw new JsonSerializationException("Invalid setting enum: " + property.Name, error); }
            if (!Enum.IsDefined(enumType, value))
                throw new JsonSerializationException("Invalid setting enum: " + property.Name);
        }

        private static void ReadKnown(JObject profile, string name, Action<JToken> set)
        {
            JToken? token = profile[name];
            if (token != null) set(token);
        }

        private static bool ReadBoolean(JToken token)
        {
            if (token == null || token.Type != JTokenType.Boolean)
                throw new JsonSerializationException("Invalid setting type");
            return token.Value<bool>();
        }

        private static float ReadNumber(JToken token, string name)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new JsonSerializationException("Invalid setting type: " + name);
            try
            {
                float value = token.Value<float>();
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new JsonSerializationException("Invalid setting number: " + name);
                return value;
            }
            catch (JsonSerializationException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new JsonSerializationException("Invalid setting number: " + name, error);
            }
        }

        private static int ReadEnum(JToken token, Type enumType, string name)
        {
            if (token == null || token.Type != JTokenType.Integer)
                throw new JsonSerializationException("Invalid setting enum: " + name);
            int value;
            try { value = token.Value<int>(); }
            catch (Exception error) { throw new JsonSerializationException("Invalid setting enum: " + name, error); }
            if (!Enum.IsDefined(enumType, value))
                throw new JsonSerializationException("Invalid setting enum: " + name);
            return value;
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
