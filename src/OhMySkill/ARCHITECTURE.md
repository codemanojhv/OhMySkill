# Oh My Skill native build

The application captures narration, bounded trajectory context, screen evidence, and interaction semantics as one local trace. Each action receives a frame before and after the interaction; narration is segmented and attached to nearby actions. AI interprets up to six action pairs at a time, synthesizes the complete ordered trajectory, and critic-checks the result before review.

The final review creates only `SKILL.md` and `USE_THIS_SKILL.txt`. Temporary evidence is encrypted and deleted after save.
