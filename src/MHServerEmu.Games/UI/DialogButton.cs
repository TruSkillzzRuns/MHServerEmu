using Gazillion;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.UI
{
    public class DialogButton
    {
        public GameDialogResultEnum Type { get; }
        public LocaleStringMessageHandler ButtonText { get; }
        public ButtonStyle Style { get; }
        public bool Enabled { get; set; }

        public DialogButton(GameDialogResultEnum type, LocaleStringId buttonText, ButtonStyle style, bool enabled)
        {
            Type = type;
            ButtonText = new(buttonText);
            Style = style;
            Enabled = enabled;
        }

        public NetStructDialogButton ToProtobuf()
        {
            var builder = new NetStructDialogButton.Builder()
                .SetType(Type)
                .SetFormatString(ButtonText.ToProtobuf())
                .SetStyle((uint)Style)
                .SetEnabled(Enabled);

#if GAME_VERSION_1_53
            // 1.53 added a new REQUIRED "Hold" bool field to
            // NetStructDialogButton (press-and-hold confirmation UX) that
            // doesn't exist on 1.48/1.52. Leaving it unset made Build()
            // throw UninitializedMessageException and crash the whole game
            // instance the moment any dialog with a button was shown on
            // 1.53 -- confirmed live 2026-07-28 (Trial of the Impossible's
            // Nick Fury dialog). Plain click, not hold-to-confirm.
            builder.SetHold(false);
#endif

            return builder.Build();
        }
    }
}
