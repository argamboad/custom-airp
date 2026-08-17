namespace Airp.Application.Text;

/// <summary>One emoji and the name typed to reach it.</summary>
/// <param name="Name">The shortcode without its colons, such as <c>smile</c>.</param>
/// <param name="Emoji">The emoji itself.</param>
/// <param name="Keywords">Extra words that should also find it, space separated.</param>
public readonly record struct EmojiShortcode(string Name, string Emoji, string Keywords = "")
{
    /// <summary>The text completion is ranked against: the name plus its keywords.</summary>
    public string SearchText => Keywords.Length == 0 ? Name : $"{Name} {Keywords}";
}

/// <summary>
/// The <c>:shortcode:</c> table behind the composer's emoji completion.
/// </summary>
/// <remarks>
/// <para>
/// Typing emoji directly is not really available here. The console hands over one UTF-16 code
/// unit per key press, so an emoji key would arrive as two unrelated presses carrying half a
/// surrogate pair each, and the platform pickers route through paste rather than the keyboard.
/// Naming them in ASCII sidesteps all of that: everything the user presses is a letter.
/// </para>
/// <para>
/// The list is curated rather than exhaustive. A complete Unicode table runs to several
/// thousand entries, and past the first couple of hundred the additions are things nobody
/// reaches for by name — the cost is a longer list to scan and worse ranking for the emoji
/// people actually want. Names follow the shortcodes used by Slack, Discord and GitHub, so
/// the ones already in a user's fingers work here too.
/// </para>
/// </remarks>
public static class EmojiShortcodes
{
    /// <summary>Every shortcode, in a stable order that ranking uses to break ties.</summary>
    public static IReadOnlyList<EmojiShortcode> All { get; } =
    [
        // Faces — smiling and laughing
        new("smile", "\U0001F604", "happy joy grin"),
        new("smiley", "\U0001F603", "happy joy"),
        new("grin", "\U0001F601", "happy"),
        new("laughing", "\U0001F606", "haha lol"),
        new("joy", "\U0001F602", "laugh cry tears lol"),
        new("rofl", "\U0001F923", "rolling laughing floor"),
        new("sweat_smile", "\U0001F605", "relief phew"),
        new("slightly_smiling_face", "\U0001F642", "slight smile"),
        new("upside_down_face", "\U0001F643", "irony sarcasm"),
        new("wink", "\U0001F609", "flirt"),
        new("blush", "\U0001F60A", "shy happy"),
        new("innocent", "\U0001F607", "angel halo"),

        // Faces — affection and delight
        new("heart_eyes", "\U0001F60D", "love adore"),
        new("kissing_heart", "\U0001F618", "kiss love"),
        new("smiling_face_with_three_hearts", "\U0001F970", "adore love"),
        new("star_struck", "\U0001F929", "amazed wow"),
        new("hugs", "\U0001F917", "hug embrace"),
        new("relieved", "\U0001F60C", "content calm"),
        new("yum", "\U0001F60B", "delicious tasty"),

        // Faces — playful
        new("stuck_out_tongue", "\U0001F61B", "tongue cheeky"),
        new("stuck_out_tongue_winking_eye", "\U0001F61C", "cheeky joke"),
        new("zany_face", "\U0001F92A", "silly wild"),
        new("sunglasses", "\U0001F60E", "cool"),
        new("nerd_face", "\U0001F913", "geek glasses"),
        new("partying_face", "\U0001F973", "celebrate party"),
        new("smirk", "\U0001F60F", "sly"),

        // Faces — doubt and thought
        new("thinking", "\U0001F914", "hmm think consider"),
        new("raised_eyebrow", "\U0001F928", "skeptical doubt"),
        new("neutral_face", "\U0001F610", "meh blank"),
        new("expressionless", "\U0001F611", "blank"),
        new("no_mouth", "\U0001F636", "silent speechless"),
        new("unamused", "\U0001F612", "meh unimpressed"),
        new("roll_eyes", "\U0001F644", "eyeroll whatever"),
        new("grimacing", "\U0001F62C", "awkward eek"),
        new("zipper_mouth_face", "\U0001F910", "secret quiet"),
        new("shushing_face", "\U0001F92B", "quiet secret"),

        // Faces — worry and distress
        new("pensive", "\U0001F614", "sad thoughtful"),
        new("confused", "\U0001F615", "unsure"),
        new("worried", "\U0001F61F", "concern"),
        new("frowning_face", "\U00002639\U0000FE0F", "sad"),
        new("cry", "\U0001F622", "sad tear"),
        new("sob", "\U0001F62D", "crying bawling"),
        new("disappointed", "\U0001F61E", "sad let down"),
        new("weary", "\U0001F629", "tired exhausted"),
        new("tired_face", "\U0001F62B", "exhausted"),
        new("sleepy", "\U0001F62A", "tired"),
        new("sleeping", "\U0001F634", "asleep zzz"),
        new("fearful", "\U0001F628", "scared"),
        new("cold_sweat", "\U0001F630", "anxious nervous"),
        new("scream", "\U0001F631", "horror shock"),
        new("flushed", "\U0001F633", "embarrassed blush"),
        new("astonished", "\U0001F632", "shocked wow"),
        new("open_mouth", "\U0001F62E", "surprised"),
        new("hushed", "\U0001F62F", "surprised quiet"),
        new("exploding_head", "\U0001F92F", "mind blown"),

        // Faces — displeasure
        new("angry", "\U0001F620", "mad cross"),
        new("rage", "\U0001F621", "furious mad"),
        new("triumph", "\U0001F624", "huff steam"),
        new("face_with_symbols_over_mouth", "\U0001F92C", "swearing cursing"),
        new("nauseated_face", "\U0001F922", "sick disgusted"),
        new("face_vomiting", "\U0001F92E", "sick"),
        new("sick", "\U0001F912", "thermometer ill"),
        new("mask", "\U0001F637", "unwell"),
        new("dizzy_face", "\U0001F635", "stunned"),
        new("skull", "\U0001F480", "dead"),
        new("ghost", "\U0001F47B", "boo spooky"),
        new("alien", "\U0001F47D", "ufo space"),
        new("robot", "\U0001F916", "bot ai"),
        new("clown_face", "\U0001F921", "joker"),
        new("poop", "\U0001F4A9", "rubbish"),

        // Cats and animals
        new("cat", "\U0001F431", "kitten"),
        new("heart_eyes_cat", "\U0001F63B", "love cat"),
        new("joy_cat", "\U0001F639", "laughing cat"),
        new("dog", "\U0001F436", "puppy"),
        new("fox_face", "\U0001F98A", "fox"),
        new("bear", "\U0001F43B", ""),
        new("panda_face", "\U0001F43C", "panda"),
        new("monkey", "\U0001F412", ""),
        new("see_no_evil", "\U0001F648", "monkey"),
        new("hear_no_evil", "\U0001F649", "monkey"),
        new("speak_no_evil", "\U0001F64A", "monkey"),
        new("unicorn", "\U0001F984", "magic"),
        new("bug", "\U0001F41B", "insect caterpillar"),
        new("beetle", "\U0001FAB2", "insect"),
        new("spider", "\U0001F577\U0000FE0F", ""),
        new("snake", "\U0001F40D", ""),
        new("penguin", "\U0001F427", ""),
        new("bird", "\U0001F426", ""),
        new("whale", "\U0001F433", ""),
        new("fish", "\U0001F41F", ""),
        new("octopus", "\U0001F419", ""),
        new("butterfly", "\U0001F98B", ""),

        // Gestures and people
        new("thumbsup", "\U0001F44D", "+1 yes like approve"),
        new("thumbsdown", "\U0001F44E", "-1 no dislike"),
        new("ok_hand", "\U0001F44C", "fine good"),
        new("clap", "\U0001F44F", "applause bravo"),
        new("raised_hands", "\U0001F64C", "praise celebrate"),
        new("pray", "\U0001F64F", "please thanks"),
        new("wave", "\U0001F44B", "hello goodbye hi"),
        new("handshake", "\U0001F91D", "deal agree"),
        new("muscle", "\U0001F4AA", "strong flex"),
        new("point_right", "\U0001F449", "this"),
        new("point_left", "\U0001F448", ""),
        new("point_up", "\U0001F446", ""),
        new("point_down", "\U0001F447", ""),
        new("crossed_fingers", "\U0001F91E", "luck hope"),
        new("v", "\U0000270C\U0000FE0F", "peace victory"),
        new("fist", "\U0000270A", "solidarity"),
        new("writing_hand", "\U0000270D\U0000FE0F", "write note"),
        new("eyes", "\U0001F440", "look watching"),
        new("brain", "\U0001F9E0", "mind smart"),
        new("shrug", "\U0001F937", "dunno whatever"),
        new("facepalm", "\U0001F926", "sigh oops"),
        new("dancer", "\U0001F483", "dancing celebrate"),

        // Hearts and symbols
        new("heart", "\U00002764\U0000FE0F", "love red"),
        new("orange_heart", "\U0001F9E1", "love"),
        new("yellow_heart", "\U0001F49B", "love"),
        new("green_heart", "\U0001F49A", "love"),
        new("blue_heart", "\U0001F499", "love"),
        new("purple_heart", "\U0001F49C", "love"),
        new("black_heart", "\U0001F5A4", "love"),
        new("broken_heart", "\U0001F494", "sad heartbreak"),
        new("sparkling_heart", "\U0001F496", "love"),
        new("heartpulse", "\U0001F497", "love"),
        new("star", "\U00002B50", "favourite"),
        new("sparkles", "\U00002728", "shiny magic new"),
        new("dizzy", "\U0001F4AB", "star"),
        new("boom", "\U0001F4A5", "explosion collision"),
        new("fire", "\U0001F525", "lit hot burn"),
        new("zap", "\U000026A1", "lightning fast"),
        new("100", "\U0001F4AF", "hundred perfect"),
        new("tada", "\U0001F389", "party celebrate congrats"),
        new("confetti_ball", "\U0001F38A", "party celebrate"),
        new("balloon", "\U0001F388", "party"),
        new("gift", "\U0001F381", "present"),
        new("trophy", "\U0001F3C6", "win award"),
        new("medal", "\U0001F3C5", "award"),
        new("crown", "\U0001F451", "king queen"),
        new("gem", "\U0001F48E", "diamond"),
        new("rocket", "\U0001F680", "launch ship fast"),
        new("checkered_flag", "\U0001F3C1", "finish race"),

        // Marks and arrows
        new("white_check_mark", "\U00002705", "done yes tick"),
        new("heavy_check_mark", "\U00002714\U0000FE0F", "tick done"),
        new("x", "\U0000274C", "no cross wrong"),
        new("warning", "\U000026A0\U0000FE0F", "caution careful"),
        new("question", "\U00002753", "help"),
        new("exclamation", "\U00002757", "important"),
        new("bangbang", "\U0000203C\U0000FE0F", "important"),
        new("no_entry", "\U000026D4", "stop forbidden"),
        new("recycle", "\U0000267B\U0000FE0F", "reuse"),
        new("infinity", "\U0000267E\U0000FE0F", "forever"),

        // Objects and places
        new("bulb", "\U0001F4A1", "idea light"),
        new("wrench", "\U0001F527", "fix tool"),
        new("hammer", "\U0001F528", "build tool"),
        new("gear", "\U00002699\U0000FE0F", "settings cog"),
        new("lock", "\U0001F512", "secure private"),
        new("key", "\U0001F511", "unlock"),
        new("mag", "\U0001F50D", "search find"),
        new("bell", "\U0001F514", "notify alert"),
        new("no_bell", "\U0001F515", "mute silent"),
        new("hourglass", "\U000023F3", "wait time"),
        new("alarm_clock", "\U000023F0", "time wake"),
        new("calendar", "\U0001F4C5", "date schedule"),
        new("memo", "\U0001F4DD", "note write"),
        new("books", "\U0001F4DA", "read study"),
        new("computer", "\U0001F4BB", "laptop code"),
        new("iphone", "\U0001F4F1", "phone mobile"),
        new("camera", "\U0001F4F7", "photo"),
        new("headphones", "\U0001F3A7", "music listen"),
        new("musical_note", "\U0001F3B5", "music song"),
        new("microphone", "\U0001F3A4", "sing voice"),
        new("art", "\U0001F3A8", "paint palette"),
        new("clapper", "\U0001F3AC", "film movie"),
        new("game_die", "\U0001F3B2", "dice random"),
        new("video_game", "\U0001F3AE", "gaming controller"),
        new("envelope", "\U00002709\U0000FE0F", "mail letter"),
        new("package", "\U0001F4E6", "box delivery"),
        new("money_with_wings", "\U0001F4B8", "spend cost"),
        new("chart_with_upwards_trend", "\U0001F4C8", "growth up"),
        new("chart_with_downwards_trend", "\U0001F4C9", "decline down"),
        new("bar_chart", "\U0001F4CA", "stats data"),
        new("pushpin", "\U0001F4CC", "pin"),
        new("paperclip", "\U0001F4CE", "attach"),
        new("link", "\U0001F517", "url chain"),
        new("scissors", "\U00002702\U0000FE0F", "cut"),
        new("wastebasket", "\U0001F5D1\U0000FE0F", "bin delete trash"),

        // Food and drink
        new("coffee", "\U00002615", "tea drink caffeine"),
        new("tea", "\U0001F375", "drink"),
        new("beer", "\U0001F37A", "drink pub"),
        new("wine_glass", "\U0001F377", "drink"),
        new("champagne", "\U0001F37E", "celebrate drink"),
        new("cake", "\U0001F370", "dessert"),
        new("birthday", "\U0001F382", "cake celebrate"),
        new("cookie", "\U0001F36A", "biscuit"),
        new("chocolate_bar", "\U0001F36B", "sweet"),
        new("doughnut", "\U0001F369", "donut"),
        new("pizza", "\U0001F355", "food"),
        new("hamburger", "\U0001F354", "burger food"),
        new("fries", "\U0001F35F", "chips food"),
        new("taco", "\U0001F32E", "food"),
        new("apple", "\U0001F34E", "fruit"),
        new("banana", "\U0001F34C", "fruit"),
        new("strawberry", "\U0001F353", "fruit"),
        new("avocado", "\U0001F951", "food"),
        new("popcorn", "\U0001F37F", "cinema snack"),
        new("salt", "\U0001F9C2", "seasoning"),

        // Nature and weather
        new("sunny", "\U00002600\U0000FE0F", "sun clear weather"),
        new("cloud", "\U00002601\U0000FE0F", "weather"),
        new("rain_cloud", "\U0001F327\U0000FE0F", "weather wet"),
        new("snowflake", "\U00002744\U0000FE0F", "cold winter"),
        new("rainbow", "\U0001F308", "colour pride"),
        new("ocean", "\U0001F30A", "wave sea"),
        new("earth_africa", "\U0001F30D", "world globe"),
        new("moon", "\U0001F319", "night"),
        new("sun_with_face", "\U0001F31E", "sunshine"),
        new("seedling", "\U0001F331", "plant grow"),
        new("herb", "\U0001F33F", "plant leaf"),
        new("four_leaf_clover", "\U0001F340", "luck"),
        new("maple_leaf", "\U0001F341", "autumn fall"),
        new("cactus", "\U0001F335", "plant"),
        new("evergreen_tree", "\U0001F332", "forest pine"),
        new("mountain", "\U000026F0\U0000FE0F", "hill peak"),
        new("rose", "\U0001F339", "flower"),
        new("sunflower", "\U0001F33B", "flower"),
        new("bouquet", "\U0001F490", "flowers"),
    ];

    private static readonly Dictionary<string, string> ByName =
        All.ToDictionary(static e => e.Name, static e => e.Emoji, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds the emoji for an exact shortcode name.</summary>
    /// <param name="name">The name without colons; matched case-insensitively.</param>
    /// <returns>The emoji, or <see langword="null"/> when the name is not in the table.</returns>
    public static string? Find(string? name)
        => name is { Length: > 0 } && ByName.TryGetValue(name, out var emoji) ? emoji : null;

    /// <summary>
    /// Ranks shortcodes against what has been typed so far.
    /// </summary>
    /// <remarks>
    /// An exact name is forced to the front regardless of score, because a user who has typed
    /// a whole shortcode has told you exactly which one they mean and no amount of fuzzy
    /// cleverness should second-guess that.
    /// </remarks>
    /// <param name="query">The partial name, without colons. Empty returns the head of the list.</param>
    /// <param name="limit">Maximum results.</param>
    /// <returns>The best matches, in order.</returns>
    public static IReadOnlyList<EmojiShortcode> Suggest(string? query, int limit = 8)
    {
        if (limit <= 0)
        {
            return [];
        }

        if (string.IsNullOrEmpty(query))
        {
            return [.. All.Take(limit)];
        }

        var ranked = FuzzyMatcher.Rank(All, query, static e => e.SearchText);

        var exact = ranked.FirstOrDefault(e => string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase));
        if (exact.Name is { Length: > 0 })
        {
            return [exact, .. ranked.Where(e => e.Name != exact.Name).Take(limit - 1)];
        }

        return [.. ranked.Take(limit)];
    }
}
