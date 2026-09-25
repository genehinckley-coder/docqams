using DocQaMs.Core.Answering;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Claude.Tests.TestSupport;

/// <summary>
/// The NotRag prototype's two synthetic request-shape fixtures (<c>spike_request_shape.py</c>), with LF
/// line endings, as production sends. They are modeled on the corpus's layout, not real regulation.
/// </summary>
/// <remarks>
/// Document A holds one character outside the BMP (U+1D400), so every citation after it tells code-point
/// offsets from UTF-16 offsets.
/// </remarks>
internal static class SpikeFixtures
{
    public const string FileA = "ADMINISTRATIVE CODE 41 175.htm";

    public const string FileB = "ADMINISTRATIVE CODE 41 174.htm";

    /// <summary>MATHEMATICAL BOLD CAPITAL A: one code point, two UTF-16 units.</summary>
    public const string NonBmp = "\U0001D400";

    private const string TemplateA =
        """
        TITLE 41: FIRE PROTECTION
        CHAPTER I: OFFICE OF THE STATE FIRE MARSHAL
        PART 175 TECHNICAL REQUIREMENTS FOR UNDERGROUND STORAGE TANKS

        {N}

        Section 175.410{N} Tank Containment Sumps

        {N}

        (Figure {F} in Appendix B illustrates a typical tank containment sump and is
        not reproduced in this text.)

        {N}

        a){P} Applicability. This Section applies to every UST system installed after
        the effective date of this Part and to any UST system on which work described
        in {S}{N}175.300(f) is performed.

        {N}

        b){P} Tank Containment Sumps. A tank containment sump shall be installed on
        every tank top where a submersible pump, piping connection or interstitial
        monitoring port is located. The sump shall be liquid tight{M}including all
        penetration fittings and the lid{M}and shall be constructed of a material
        compatible with the product stored.

        {N}

        c){P} Capacity. Each tank containment sump shall have a minimum capacity of
        5{N}gallons. Spill containment devices installed as replacement inserts shall
        have a capacity of at least 3.5{N}gallons.

        {N}

        d){P} Monitoring. Each tank containment sump shall be equipped with a liquid
        sensor that is connected to the automatic tank gauge (ATG) control module and
        that triggers an audible and visual alarm when liquid is detected. The sensor
        shall be positioned within 1{N}inch of the lowest point of the sump floor.

        {N}

        e){P} Testing. Each tank containment sump shall be tested for liquid tightness
        at installation and at least once every 3{N}years thereafter, using a
        hydrostatic test in which the sump is filled to a level 4{D}6 inches above the
        highest penetration fitting and observed for not less than 1{N}hour. A drop in
        liquid level of more than 1/8 inch during the observation period constitutes a
        failure.

        {N}

        f){P} Repair. A sump that fails a test under subsection (e) shall be repaired
        or replaced within 30{N}days and shall be retested in accordance with
        {S}{N}175.410(e) before the UST system is returned to service.

        {N}

        g){P} Records. The owner or operator shall maintain records of each
        installation, test and repair required by this Section for the operating life
        of the UST system and shall make them available to the Office of the State Fire
        Marshal upon request.

        {N}

        (Source: Amended at 47 Ill. Reg. 12345, effective July 1, 2023)
        """;

    private const string TemplateB =
        """
        TITLE 41: FIRE PROTECTION
        CHAPTER I: OFFICE OF THE STATE FIRE MARSHAL
        PART 174 GENERAL REQUIREMENTS FOR UNDERGROUND STORAGE TANKS

        {N}

        Section 174.120{N} Permits Required

        {N}

        (Table {F} referenced in subsection (c) appears in Appendix A and is not
        reproduced in this text.)

        {N}

        a){P} General. No person shall install, repair, upgrade or remove any UST
        system, or any component of a UST system, without first obtaining a permit from
        the Office of the State Fire Marshal, except as provided in subsection (d).

        {N}

        b){P} Application. An application for a permit shall be submitted by a
        contractor licensed under 41 Ill. Adm. Code 172 at least 10{N}business days
        before work begins. The application shall identify the facility, the owner and
        operator, and each tank, line and sump affected by the work.

        {N}

        c){P} Activities Requiring a Permit. Activities requiring a permit include, but
        are not limited to:

        {N}

        1){P} installation of a new tank, line or dispenser;

        {N}

        2){P} any activity that triggers the requirement to install a tank
        containment sump under {S}{N}175.410(b);

        {N}

        3){P} replacement of an entire shear valve{M}replacement of only the
        bolted-on top section of a shear valve does not require a permit; and

        {N}

        4){P} any pipe or flex connector work.

        {N}

        d){P} Exceptions. No permit is required for like-for-like replacement of drop
        tube valves, ATG probes, mechanical or electronic line leak detectors, or
        interstitial monitoring sensors. The exceptions listed in this subsection are
        the only exceptions from the permit requirement.

        {N}

        e){P} Duration. A permit is valid for 180{N}days from the date of issuance and
        may be extended once, for a period of 90{N}days, upon written request received
        before the permit expires.

        {N}

        (Source: Amended at 47 Ill. Reg. 12346, effective July 1, 2023)
        """;

    public static string TextA { get; } = Render(TemplateA);

    public static string TextB { get; } = Render(TemplateB);

    /// <summary>Both fixtures, titled and contextualized exactly as production renders corpus files.</summary>
    public static IReadOnlyList<RequestDocument> Documents { get; } = [Document(FileA, TextA), Document(FileB, TextB)];

    private static RequestDocument Document(string fileName, string text) =>
        new(CacheAffecting.DocumentLabels[fileName], CacheAffecting.RenderDocumentContext(fileName), text);

    // Characters under test are escapes, so none is invisible in source. The raw literals take this
    // file's line endings, which git may have turned into CRLF on checkout.
    private static string Render(string template) => template
        .ReplaceLineEndings("\n")
        .Replace("{P}", new string('\u00A0', 7), StringComparison.Ordinal)
        .Replace("{N}", "\u00A0", StringComparison.Ordinal)
        .Replace("{S}", "\u00A7", StringComparison.Ordinal)
        .Replace("{M}", "\u2014", StringComparison.Ordinal)
        .Replace("{D}", "\u2013", StringComparison.Ordinal)
        .Replace("{F}", NonBmp, StringComparison.Ordinal);
}
