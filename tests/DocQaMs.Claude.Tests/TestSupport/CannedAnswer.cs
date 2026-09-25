namespace DocQaMs.Claude.Tests.TestSupport;

/// <summary>
/// A streamed answer in the Messages API's event format, covering every event and block kind this app
/// receives: a thinking block with a signature, a redacted thinking block, uncited and cited text
/// blocks, and an empty text block (which must be dropped on replay).
/// </summary>
internal static class CannedAnswer
{
    public const string DocumentTitle = "41 Ill. Adm. Code 175: Technical Requirements for Underground Storage Tanks";

    public const long Write1h = 2_080;

    public const long Write5m = 20;

    public const string Thinking = "The user asks about sumps. Section 175.410 applies.";

    public const string Signature = "c2lnbmF0dXJlLTE=";

    /// <summary>The first citation's quoted text; the space before "gallons" is U+00A0, as in the corpus.</summary>
    public const string CitedText1 = "minimum capacity of\n5\u00A0gallons";

    public const string CitedText2 = "at least 3.5\u00A0gallons";

    /// <summary>Up to and including the first thinking delta: what arrives before the model writes much.</summary>
    public static IReadOnlyList<string> Opening { get; } =
    [
        """{"type":"message_start","message":{"id":"msg_canned","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":12,"cache_creation_input_tokens":2100,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_1h_input_tokens":2080,"ephemeral_5m_input_tokens":20},"output_tokens":1}}}""",
        """{"type":"ping"}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"The user asks about sumps. "}}""",
    ];

    public static IReadOnlyList<string> Events { get; } =
    [
        .. Opening,
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Section 175.410 applies."}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"c2lnbmF0dXJlLTE="}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"redacted_thinking","data":"cmVkYWN0ZWQ="}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"content_block_start","index":2,"content_block":{"type":"text","text":"","citations":null}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Sumps must hold "}}""",
        """{"type":"content_block_stop","index":2}""",
        """{"type":"content_block_start","index":3,"content_block":{"type":"text","text":"","citations":[]}}""",
        """{"type":"content_block_delta","index":3,"delta":{"type":"citations_delta","citation":{"type":"char_location","cited_text":"minimum capacity of\n5\u00A0gallons","document_index":0,"document_title":"41 Ill. Adm. Code 175: Technical Requirements for Underground Storage Tanks","start_char_index":851,"end_char_index":880}}}""",
        """{"type":"content_block_delta","index":3,"delta":{"type":"text_delta","text":"a minimum of 5 gallons"}}""",
        """{"type":"content_block_delta","index":3,"delta":{"type":"citations_delta","citation":{"type":"char_location","cited_text":"at least 3.5\u00A0gallons","document_index":0,"document_title":"41 Ill. Adm. Code 175: Technical Requirements for Underground Storage Tanks","start_char_index":950,"end_char_index":970}}}""",
        """{"type":"content_block_stop","index":3}""",
        """{"type":"content_block_start","index":4,"content_block":{"type":"text","text":"","citations":null}}""",
        """{"type":"content_block_stop","index":4}""",
        """{"type":"content_block_start","index":5,"content_block":{"type":"text","text":"","citations":null}}""",
        """{"type":"content_block_delta","index":5,"delta":{"type":"text_delta","text":"."}}""",
        """{"type":"content_block_stop","index":5}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":57}}""",
        """{"type":"message_stop"}""",
    ];

    /// <summary>
    /// The events a consumer should see, in order: every event except <c>ping</c>, with content block
    /// deltas named by their delta type.
    /// </summary>
    public static IReadOnlyList<string> ForwardedEventNames { get; } =
    [
        "message_start",
        "content_block_start", "content_block_delta:thinking_delta", "content_block_delta:thinking_delta",
        "content_block_delta:signature_delta", "content_block_stop",
        "content_block_start", "content_block_stop",
        "content_block_start", "content_block_delta:text_delta", "content_block_stop",
        "content_block_start", "content_block_delta:citations_delta", "content_block_delta:text_delta",
        "content_block_delta:citations_delta", "content_block_stop",
        "content_block_start", "content_block_stop",
        "content_block_start", "content_block_delta:text_delta", "content_block_stop",
        "message_delta",
        "message_stop",
    ];

    /// <summary>The reply as it must be sent back: the response's content minus its empty text block.</summary>
    public const string ReplayedContentJson =
        """
        [
          {"type":"thinking","thinking":"The user asks about sumps. Section 175.410 applies.","signature":"c2lnbmF0dXJlLTE="},
          {"type":"redacted_thinking","data":"cmVkYWN0ZWQ="},
          {"type":"text","text":"Sumps must hold "},
          {"type":"text","text":"a minimum of 5 gallons","citations":[
            {"type":"char_location","cited_text":"minimum capacity of\n5\u00A0gallons","document_index":0,"document_title":"41 Ill. Adm. Code 175: Technical Requirements for Underground Storage Tanks","start_char_index":851,"end_char_index":880},
            {"type":"char_location","cited_text":"at least 3.5\u00A0gallons","document_index":0,"document_title":"41 Ill. Adm. Code 175: Technical Requirements for Underground Storage Tanks","start_char_index":950,"end_char_index":970}
          ]},
          {"type":"text","text":"."}
        ]
        """;
}
