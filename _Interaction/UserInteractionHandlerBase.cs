﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Threading;
using System.Threading.Tasks;
using DPoint = System.Drawing.Point;

using TShockAPI;

namespace Terraria.Plugins.CoderCow {
  public abstract class UserInteractionHandlerBase: IDisposable {
    #region [Constants]
    public const int CommandInteractionTimeout = 1200; // In frames
    private const int UpdateFrameRate = 60;
    #endregion

    #region [Property: PluginTrace]
    private readonly PluginTrace pluginTrace;

    protected PluginTrace PluginTrace {
      get { return this.pluginTrace; }
    }
    #endregion

    #region [Property: RegisteredCommands]
    private readonly Collection<Command> registeredCommands;

    protected Collection<Command> RegisteredCommands {
      get { return this.registeredCommands; }
    }
    #endregion

    private readonly Dictionary<TSPlayer,PlayerCommandInteraction> activeCommandInteractions = 
      new Dictionary<TSPlayer,PlayerCommandInteraction>();
    private readonly object activeCommandInteractionsLock = new object();
    private readonly Dictionary<Command,CommandDelegate> commandHelpCallbacks;
    private readonly Command originalHelpCommand;
    private readonly Command customHelpCommand;


    #region [Method: Constructor]
    protected UserInteractionHandlerBase(PluginTrace pluginTrace) {
      this.pluginTrace = pluginTrace;
      this.registeredCommands = new Collection<Command>();
      this.activeCommandInteractions = new Dictionary<TSPlayer,PlayerCommandInteraction>();
      this.commandHelpCallbacks = new Dictionary<Command,CommandDelegate>();

      try {
        this.originalHelpCommand = TShockAPI.Commands.ChatCommands.First(cmd => cmd.Name == "help");
      } catch (InvalidOperationException) {
        this.PluginTrace.WriteLineError("Failed overriding /help command. Make sure you're creating the UserInteractionHandler instance after TShock has initialized.");
      }

      if (this.originalHelpCommand != null) {
        TShockAPI.Commands.ChatCommands.Remove(this.originalHelpCommand);

        this.customHelpCommand = new Command(this.CustomHelpCommand_Exec, "help", "cmds");
        TShockAPI.Commands.ChatCommands.Add(this.customHelpCommand);
      }
    }
    #endregion

    #region [Methods: RegisterCommand, DeregisterCommand, CustomHelpCommand_Exec]
    protected void RegisterCommand(Command tshockCommand, CommandDelegate helpCallback) {
      Contract.Requires<ObjectDisposedException>(!this.IsDisposed);
      Contract.Requires<ArgumentNullException>(tshockCommand != null);
      Contract.Requires<ArgumentNullException>(helpCallback != null);
      
      if (TShockAPI.Commands.ChatCommands.Contains(tshockCommand))
        throw new ArgumentException("Command already registered.", "tshockCommand");

      TShockAPI.Commands.ChatCommands.Add(tshockCommand);
      this.commandHelpCallbacks.Add(tshockCommand, helpCallback);
    }

    protected void DeregisterCommand(Command tshockCommand) {
      Contract.Requires<ArgumentNullException>(tshockCommand != null);

      if (!TShockAPI.Commands.ChatCommands.Contains(tshockCommand))
        throw new InvalidOperationException("Command is not registered.");

      if (this.commandHelpCallbacks.ContainsKey(tshockCommand))
        this.commandHelpCallbacks.Remove(tshockCommand);
    }

    private void CustomHelpCommand_Exec(CommandArgs args) {
      int dummy;
      if (args.Parameters.Count > 0 && !int.TryParse(args.Parameters[0], out dummy)) {
        string commandName = args.Parameters[0].ToLowerInvariant();

        CommandDelegate helpCallback = null;
        try {
          KeyValuePair<Command,CommandDelegate> commandHelpPair = this.commandHelpCallbacks.First(
            pair => pair.Key.Names.Contains(commandName)
          );
          helpCallback = commandHelpPair.Value;
        } catch (InvalidOperationException) {}
          
        if (helpCallback != null) {
          try {
            helpCallback(args);
            return;
          } catch (Exception ex) {
            this.PluginTrace.WriteLineError(
              "The help callback delegate of command \"/{0}\" has thrown an exception: {1}", commandName, ex
            );
          }
        // Did we override TShock's real help command?
        } else if (this.originalHelpCommand.Names.Count == 1) {
          args.Player.SendErrorMessage("There is no help for this command available.");
          return;
        }
      }

      this.originalHelpCommand.Run(args.Message, args.Player, args.Parameters);
    }
    #endregion

    #region [Method: StartOrResetCommandInteraction, StopInteraction]
    protected PlayerCommandInteraction StartOrResetCommandInteraction(TSPlayer forPlayer) {
      Contract.Requires<ObjectDisposedException>(!this.IsDisposed);
      Contract.Requires<ArgumentNullException>(forPlayer != null);

      PlayerCommandInteraction newInteraction = new PlayerCommandInteraction(forPlayer);
      newInteraction.framesLeft = UserInteractionHandlerBase.CommandInteractionTimeout;

      lock (this.activeCommandInteractionsLock) {
        if (!this.activeCommandInteractions.ContainsKey(forPlayer))
          this.activeCommandInteractions.Add(forPlayer, newInteraction);
        else
          this.activeCommandInteractions[forPlayer] = newInteraction;
      }

      newInteraction.TimeoutTask = Task.Factory.StartNew(() => {
        if (this.IsDisposed)
          return;

        while (newInteraction.framesLeft > 0) {
          if (this.IsDisposed)
            return;

          newInteraction.framesLeft -= 10;
          Thread.Sleep(100);
        }

        lock (this.activeCommandInteractionsLock) {
          if (this.IsDisposed)
            return;

          if (!this.activeCommandInteractions.ContainsValue(newInteraction))
            return;

          if (forPlayer.ConnectionAlive && newInteraction.TimeExpiredCallback != null) {
            try {
              newInteraction.TimeExpiredCallback(forPlayer);
            } catch (Exception ex) {
              this.PluginTrace.WriteLineError("A command interaction's time expired callback has thrown an exception:\n" + ex);
            }
          }

          this.activeCommandInteractions.Remove(forPlayer);
        }
      });

      return newInteraction;
    }

    protected void StopInteraction(TSPlayer forPlayer) {
      Contract.Requires<ObjectDisposedException>(!this.IsDisposed);
      Contract.Requires<ArgumentNullException>(forPlayer != null);

      lock (this.activeCommandInteractionsLock) {
        if (this.activeCommandInteractions.ContainsKey(forPlayer))
          this.activeCommandInteractions.Remove(forPlayer);
      }
    }
    #endregion

    #region [Methods: HandleTileEdit, HandleChestGetContents, HandleSignEdit, HandleSignRead, HandleHitSwitch, HandleGameUpdate]
    public virtual bool HandleTileEdit(TSPlayer player, TileEditType editType, BlockType blockType, DPoint location, int objectStyle) {
      if (this.IsDisposed || this.activeCommandInteractions.Count == 0)
        return false;

      lock (this.activeCommandInteractionsLock) {
        PlayerCommandInteraction commandInteraction;
        // Is the player currently interacting with a command?
        if (!this.activeCommandInteractions.TryGetValue(player, out commandInteraction))
          return false;

        if (commandInteraction.TileEditCallback == null)
          return false;

        CommandInteractionResult result = commandInteraction.TileEditCallback(player, editType, blockType, location, objectStyle);
        if (commandInteraction.DoesNeverComplete)
          commandInteraction.framesLeft = UserInteractionHandlerBase.CommandInteractionTimeout;
        else if (result.IsInteractionCompleted)
          this.activeCommandInteractions.Remove(player);

        return result.IsHandled;
      }
    }

    public virtual bool HandleChestGetContents(TSPlayer player, DPoint location) {
      if (this.IsDisposed || this.activeCommandInteractions.Count == 0)
        return false;

      lock (this.activeCommandInteractionsLock) {
        PlayerCommandInteraction commandInteraction;
        // Is the player currently interacting with a command?
        if (!this.activeCommandInteractions.TryGetValue(player, out commandInteraction))
          return false;

        if (commandInteraction.ChestOpenCallback == null)
          return false;

        CommandInteractionResult result = commandInteraction.ChestOpenCallback(player, location);
        if (commandInteraction.DoesNeverComplete)
          commandInteraction.framesLeft = UserInteractionHandlerBase.CommandInteractionTimeout;
        else if (result.IsInteractionCompleted)
          this.activeCommandInteractions.Remove(player);

        return result.IsHandled;
      }
    }

    public virtual bool HandleSignEdit(TSPlayer player, short signIndex, DPoint location, string newText) {
      if (this.IsDisposed || this.activeCommandInteractions.Count == 0)
        return false;

      lock (this.activeCommandInteractionsLock) {
        PlayerCommandInteraction commandInteraction;
        // Is the player currently interacting with a command?
        if (!this.activeCommandInteractions.TryGetValue(player, out commandInteraction))
          return false;

        if (commandInteraction.SignEditCallback == null)
          return false;

        CommandInteractionResult result = commandInteraction.SignEditCallback(player, signIndex, location, newText);
        if (commandInteraction.DoesNeverComplete)
          commandInteraction.framesLeft = UserInteractionHandlerBase.CommandInteractionTimeout;
        else if (result.IsInteractionCompleted)
          this.activeCommandInteractions.Remove(player);

        return result.IsHandled;
      }
    }

    public virtual bool HandleSignRead(TSPlayer player, DPoint location) {
      if (this.IsDisposed || this.activeCommandInteractions.Count == 0)
        return false;

      lock (this.activeCommandInteractionsLock) {
        PlayerCommandInteraction commandInteraction;
        // Is the player currently interacting with a command?
        if (!this.activeCommandInteractions.TryGetValue(player, out commandInteraction))
          return false;

        if (commandInteraction.SignReadCallback == null)
          return false;
      
        CommandInteractionResult result = commandInteraction.SignReadCallback(player, location);
        if (commandInteraction.DoesNeverComplete)
          commandInteraction.framesLeft = UserInteractionHandlerBase.CommandInteractionTimeout;
        else if (result.IsInteractionCompleted)
          this.activeCommandInteractions.Remove(player);

        return result.IsHandled;
      }
    }

    public virtual bool HandleHitSwitch(TSPlayer player, DPoint location) {
      if (this.IsDisposed || this.activeCommandInteractions.Count == 0)
        return false;

      PlayerCommandInteraction commandInteraction;
      lock (this.activeCommandInteractionsLock) {
        // Is the player currently interacting with a command?
        if (!this.activeCommandInteractions.TryGetValue(player, out commandInteraction))
          return false;

        if (commandInteraction.HitSwitchCallback == null)
          return false;

        CommandInteractionResult result = commandInteraction.HitSwitchCallback(player, location);
        if (commandInteraction.DoesNeverComplete)
          commandInteraction.framesLeft = UserInteractionHandlerBase.CommandInteractionTimeout;
        else if (result.IsInteractionCompleted)
          this.activeCommandInteractions.Remove(player);

        return result.IsHandled;
      }
    }
    #endregion

    #region [IDisposable Implementation]
    private bool isDisposed;

    public bool IsDisposed {
      get { return this.isDisposed; } 
    }

    protected virtual void Dispose(bool isDisposing) {
      if (this.isDisposed)
        return;

      if (isDisposing) {
        lock (this.activeCommandInteractionsLock) {
          this.activeCommandInteractions.Clear();
        }

        if (TShockAPI.Commands.ChatCommands.Contains(this.customHelpCommand)) {
          TShockAPI.Commands.ChatCommands.Remove(this.customHelpCommand);
          TShockAPI.Commands.ChatCommands.Add(this.originalHelpCommand);
        }

        foreach (Command command in this.registeredCommands)
          if (TShockAPI.Commands.ChatCommands.Contains(command))
            TShockAPI.Commands.ChatCommands.Remove(command);
      }

      this.isDisposed = true;
    }

    public void Dispose() {
      this.Dispose(true);
      GC.SuppressFinalize(this);
    }

    ~UserInteractionHandlerBase() {
      this.Dispose(false);
    }
    #endregion
  }
}
