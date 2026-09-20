using System;

namespace CutOnce.Copilot
{
    /// <summary>
    /// Hand-mirrored from packages/schemas/dist/jsonschema/CopilotResponse.json. Shaped for Unity's
    /// JsonUtility, which cannot do dictionaries or nulls: `timings_ms` is deliberately absent (the
    /// headset does not need it — the Director page shows the timings), and a missing `action` arrives
    /// as a default-constructed object, so check <see cref="CopilotActionDto.IsPresent"/> and never null.
    /// </summary>
    [Serializable]
    public class CopilotResponseDto
    {
        public string turn_id;
        public string transcript;
        public string answer_text;
        public string[] highlight_parts;
        public string[] highlight_twins;
        public string highlight_style;   // pulse | path
        public DrawingRefDto[] drawing_refs;
        public CopilotActionDto action;
        public float confidence;
        public bool needs_clarification;
        public string audio_url;
        public bool cached;

        public bool HasAction => action != null && action.IsPresent;
    }

    [Serializable]
    public class DrawingRefDto
    {
        public string document_id;
        public string sheet_id;
        public int page;
        public string chunk_id;
        public string title;
    }

    [Serializable]
    public class CopilotActionDto
    {
        public string type;          // mark_state | log_issue | step_nav
        public string[] part_ids;
        public string new_state;     // missing | built | wrong
        public string source;        // voice
        public string issue_id;
        public string direction;     // next | back

        public bool IsPresent => !string.IsNullOrEmpty(type);
    }

    [Serializable]
    public class VerificationResultDto
    {
        public string verification_id;
        public string part_id;
        public string verdict;       // present | absent | wrong_orientation | unsure
        public float confidence;
        public string evidence;
        public string model;
        public float ms;

        /// <summary>Section 11: only a confident verdict is ever shown to the user.</summary>
        public bool IsActionable => verdict != "unsure" && confidence >= 0.8f;
    }
}
