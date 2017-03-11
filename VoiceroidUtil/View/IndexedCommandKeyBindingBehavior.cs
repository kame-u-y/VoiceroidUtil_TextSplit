﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using RucheHome.Windows.Mvvm.Behaviors;

namespace VoiceroidUtil.View
{
    /// <summary>
    /// 修飾キーと数字キーの組み合わせをコマンド列挙の各要素に割り当てるビヘイビアクラス。
    /// </summary>
    public class IndexedCommandKeyBindingBehavior
        : FrameworkElementBehavior<FrameworkElement>
    {
        /// <summary>
        /// コンストラクタ。
        /// </summary>
        public IndexedCommandKeyBindingBehavior() : base()
        {
        }

        /// <summary>
        /// Modifiers 依存関係プロパティ。
        /// </summary>
        public static readonly DependencyProperty ModifiersProperty =
            DependencyProperty.Register(
                nameof(Modifiers),
                typeof(ModifierKeys),
                typeof(IndexedCommandKeyBindingBehavior),
                new PropertyMetadata(ModifierKeys.Control));

        /// <summary>
        /// 修飾キーの組み合わせを取得または設定する。
        /// </summary>
        public ModifierKeys Modifiers
        {
            get => (ModifierKeys)this.GetValue(ModifiersProperty);
            set => this.SetValue(ModifiersProperty, value);
        }

        /// <summary>
        /// Commands 依存関係プロパティ。
        /// </summary>
        public static readonly DependencyProperty CommandsProperty =
            DependencyProperty.Register(
                nameof(Commands),
                typeof(IEnumerable<ICommand>),
                typeof(IndexedCommandKeyBindingBehavior),
                new PropertyMetadata(null));

        /// <summary>
        /// コマンド列挙を取得または設定する。
        /// </summary>
        public IEnumerable<ICommand> Commands
        {
            get => (IEnumerable<ICommand>)this.GetValue(CommandsProperty);
            set => this.SetValue(CommandsProperty, value);
        }

        #region FrameworkElementBehavior<FrameworkElement> のオーバライド

        /// <summary>
        /// FrameworkElement のロード完了時に呼び出される。
        /// </summary>
        protected override void OnAssociatedObjectLoaded()
        {
            var inputBindings = this.AssociatedObject.InputBindings;
            if (this.Commands == null || inputBindings == null)
            {
                return;
            }

            var keyBindings =
                this.Commands
                    .Take(10)
                    .SelectMany(
                        (command, index) =>
                            new[]
                            {
                                new KeyBinding(
                                    command,
                                    (index < 9) ? (Key.D1 + index) : Key.D0,
                                    this.Modifiers),
                                new KeyBinding(
                                    command,
                                    (index < 9) ? (Key.NumPad1 + index) : Key.NumPad0,
                                    this.Modifiers),
                            })
                    .ToArray();
            inputBindings.AddRange(keyBindings);
        }

        /// <summary>
        /// 自身の型のインスタンスを作成する。
        /// </summary>
        /// <returns>作成されたインスタンス。</returns>
        protected override Freezable CreateInstanceCore()
        {
            return new IndexedCommandKeyBindingBehavior();
        }

        #endregion
    }
}