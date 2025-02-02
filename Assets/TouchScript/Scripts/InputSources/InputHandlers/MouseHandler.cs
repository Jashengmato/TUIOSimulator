/*
 * @author Valentin Simonov / http://va.lent.in/
 */

using System;
using System.Collections;
using System.Collections.Generic;
using TouchScript.Pointers;
using TouchScript.Utils;
using UnityEngine;

namespace TouchScript.InputSources.InputHandlers
{
    /// <summary>
    /// Unity mouse handling implementation which can be embedded and controlled from other (input) classes.
    /// </summary>
    public class MouseHandler : IInputSource, IDisposable
    {
        #region Consts

        private enum State
        {
            /// <summary>
            /// Only mouse pointer is active
            /// </summary>
            Mouse,

            /// <summary>
            /// ALT is pressed but mouse isn't
            /// </summary>
            WaitingForFake,

            /// <summary>
            /// Mouse and fake pointers are moving together after ALT+PRESS
            /// </summary>
            MouseAndFake,

            /// <summary>
            /// After ALT+RELEASE fake pointer is stationary while mouse can move freely
            /// </summary>
            StationaryFake
        }

        public enum FakeCursorGesture
        {
            /// <summary>
            /// 无手势
            /// </summary>
            Null = 0,
            /// <summary>
            /// 虚拟点静止
            /// </summary>
            Stationary = 1,
            /// <summary>
            /// 虚拟点缩放动态
            /// </summary>
            Zoom = 2,
            /// <summary>
            /// 虚拟点平移动态
            /// </summary>
            Pan = 3,
        }

        #endregion

        #region Public properties

        /// <inheritdoc />
        public ICoordinatesRemapper CoordinatesRemapper { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether second pointer emulation using ALT+CLICK should be enabled.
        /// </summary>
        /// <value>
        /// <c>true</c> if second pointer emulation is enabled; otherwise, <c>false</c>.
        /// </value>
        public bool EmulateSecondMousePointer
        {
            get { return emulateSecondMousePointer; }
            set
            {
                emulateSecondMousePointer = value;
                if (fakeMousePointer != null) CancelPointer(fakeMousePointer, false);
            }
        }

        public FakeCursorGesture FakeGesture
        {
            get { return fakeGesture; }
        }

        #endregion

        #region Private variables

        private bool emulateSecondMousePointer = true;

        private PointerDelegate addPointer;
        private PointerDelegate updatePointer;
        private PointerDelegate pressPointer;
        private PointerDelegate releasePointer;
        private PointerDelegate removePointer;
        private PointerDelegate cancelPointer;

        private State state;
        private FakeCursorGesture fakeGesture = FakeCursorGesture.Null;
        private ObjectPool<MousePointer> mousePool;
        private MousePointer mousePointer, fakeMousePointer;
        private Vector3 mousePointPos = Vector3.zero;

        /// <summary>
        /// 虚拟点初始按下位置
        /// </summary>
        private Vector2 mouseInitPos = Vector2.zero;
        /// <summary>
        /// 虚拟点与第二鼠标点位置差
        /// </summary>
        private Vector2 mousePosOffset = Vector2.zero;
        /// <summary>
        /// 虚拟点与第二鼠标点中心位置
        /// </summary>
        private Vector2 mouseInitCenter = Vector2.zero;

        private List<KeyCode> gestureToKeyCodeMapper = new List<KeyCode>()
        {
            KeyCode.LeftAlt,
            KeyCode.LeftShift,
            KeyCode.LeftControl,
        };

        private List<KeyCode> validKeys = new List<KeyCode>()
        {
            KeyCode.LeftAlt,
            KeyCode.LeftShift,
            KeyCode.LeftControl,
        };
        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="MouseHandler" /> class.
        /// </summary>
        /// <param name="addPointer">A function called when a new pointer is detected.</param>
        /// <param name="updatePointer">A function called when a pointer is moved or its parameter is updated.</param>
        /// <param name="pressPointer">A function called when a pointer touches the surface.</param>
        /// <param name="releasePointer">A function called when a pointer is lifted off.</param>
        /// <param name="removePointer">A function called when a pointer is removed.</param>
        /// <param name="cancelPointer">A function called when a pointer is cancelled.</param>
        public MouseHandler(PointerDelegate addPointer, PointerDelegate updatePointer, PointerDelegate pressPointer, PointerDelegate releasePointer, PointerDelegate removePointer, PointerDelegate cancelPointer)
        {
            this.addPointer = addPointer;
            this.updatePointer = updatePointer;
            this.pressPointer = pressPointer;
            this.releasePointer = releasePointer;
            this.removePointer = removePointer;
            this.cancelPointer = cancelPointer;

            mousePool = new ObjectPool<MousePointer>(4, () => new MousePointer(this), null, resetPointer);

            mousePointPos = Input.mousePosition;
            mousePointer = internalAddPointer(remapCoordinates(mousePointPos));

            stateMouse();
        }

        #region Public methods

        /// <summary>
        /// Cancels the mouse pointer.
        /// </summary>
        public void CancelMousePointer()
        {
            if (mousePointer != null)
            {
                cancelPointer(mousePointer);
                mousePointer = null;
            }
        }

        /// <inheritdoc />
        public bool UpdateInput()
        {
            var pos = Input.mousePosition;
            Vector2 remappedPos = new Vector2(0, 0);
            bool updated = false;

            if (mousePointPos != pos)
            {
                remappedPos = remapCoordinates(new Vector2(pos.x, pos.y));

                if (mousePointer == null)
                {
                    mousePointer = internalAddPointer(remappedPos);
                }
                else
                {
                    mousePointer.Position = remappedPos;
                    updatePointer(mousePointer);
                }
                updated = true;
            }

            if (mousePointer == null) return false;

            var buttons = state == State.MouseAndFake ? fakeMousePointer.Buttons : mousePointer.Buttons;
            var newButtons = getMouseButtons();
            var scroll = Input.mouseScrollDelta;
            if (!Mathf.Approximately(scroll.sqrMagnitude, 0.0f))
            {
                mousePointer.ScrollDelta = scroll;
                updatePointer(mousePointer);
            }
            else
            {
                mousePointer.ScrollDelta = Vector2.zero;
            }

            if (emulateSecondMousePointer)
            {
                switch (state)
                {
                    case State.Mouse:
                        FakeCursorGesture gesture;
                        if (CheckValidKeyDown(out gesture)
                            && ((newButtons & Pointer.PointerButtonState.AnyButtonPressed) == 0))
                        {
                            fakeGesture = gesture;
                            stateWaitingForFake();
                        }
                        else
                        {
                            if (buttons != newButtons) updateButtons(buttons, newButtons);
                        }
                        break;
                    case State.WaitingForFake:
                        if (CheckValidKeyState())
                        {
                            if ((newButtons & Pointer.PointerButtonState.AnyButtonDown) != 0)
                            {
                                // A button is down while holding Alt

                                mouseInitPos = pos;
                                //Debug.Log("Set: mouseInitPos: "+ mouseInitPos.ToString());

                                fakeMousePointer = internalAddPointer(pos, newButtons, mousePointer.Flags | Pointer.FLAG_ARTIFICIAL);
                                pressPointer(fakeMousePointer);
                                stateMouseAndFake();
                            }
                        }
                        else
                        {
                            fakeGesture = FakeCursorGesture.Null;
                            stateMouse();
                        }
                        break;
                    case State.MouseAndFake:
                        if (fakeTouchReleased())
                        {
                            stateMouse();
                        }
                        else
                        {
                            if (mousePointPos != pos)
                            {
                                mouseInitPos = pos;
                                fakeMousePointer.Position = remappedPos;
                                updatePointer(fakeMousePointer);
                            }
                            if ((newButtons & Pointer.PointerButtonState.AnyButtonPressed) == 0)
                            {
                                // All buttons are released, Alt is still holding
                                stateStationaryFake();
                            }
                            else if (buttons != newButtons)
                            {
                                fakeMousePointer.Buttons = newButtons;
                                updatePointer(fakeMousePointer);
                            }
                        }
                        break;
                    case State.StationaryFake:
                        if (buttons != newButtons)
                        {
                            updateButtons(buttons, newButtons);
                        }

                        if ((newButtons & Pointer.PointerButtonState.AnyButtonDown) != 0)
                        {
                            //  Calc Center Pos And Offset
                            Vector2 mousePos = new Vector2(pos.x, pos.y);
                            mouseInitCenter = (mouseInitPos + mousePos) / 2;
                            mousePosOffset = mousePos - mouseInitPos;

                            //Debug.LogFormat("Calc Center Pos And Offset: mousePos: {0}.\n mouseInitPos: {1}, mouseInitCenter: {2}, mousePosOffset: {3}",
                            //    pos, mouseInitPos, mouseInitCenter, mousePosOffset);
                        }

                        if((newButtons & Pointer.PointerButtonState.AnyButtonPressed) != 0)
                        {
                            switch(fakeGesture)
                            {
                                case FakeCursorGesture.Stationary:
                                    //  虚拟点静态
                                    break;

                                case FakeCursorGesture.Pan:
                                    //  虚拟点平移
                                    fakeMousePointer.Position = new Vector2(pos.x, pos.y) - mousePosOffset;
                                    updatePointer(fakeMousePointer);
                                    break;

                                case FakeCursorGesture.Zoom:
                                    //  虚拟点缩放
                                    Vector2 mousePos = new Vector2(pos.x, pos.y);
                                    Vector2 offset = mousePos - mouseInitCenter;

                                    fakeMousePointer.Position = mouseInitCenter - offset;
                                    updatePointer(fakeMousePointer);
                                    break;
                            }
                        }

                        if (fakeTouchReleased())
                        {
                            stateMouse();
                        }

                        break;
                }
            }
            else
            {
                if (buttons != newButtons)
                {
                    updateButtons(buttons, newButtons);
                    updated = true;
                }
            }

            mousePointPos = pos;
            return updated;
        }

        bool CheckValidKeyDown(out FakeCursorGesture gesture)
        {
            for(int i=0; i!=validKeys.Count; i++)
            {
                if(Input.GetKeyDown(validKeys[i]) && !Input.GetKeyUp(validKeys[i]))
                {
                    gesture = (FakeCursorGesture)(i + 1);
                    return true;
                }
            }

            gesture = FakeCursorGesture.Null;
            return false;
        }

        bool CheckValidKeyState()
        {
            if (fakeGesture == FakeCursorGesture.Null)
                return true;

            return Input.GetKey(gestureToKeyCodeMapper[(int)fakeGesture-1]);
        }

        /// <inheritdoc />
        public void UpdateResolution()
        {
            TouchManager.Instance.CancelPointer(mousePointer.Id);
        }

        /// <inheritdoc />
        public bool CancelPointer(Pointer pointer, bool shouldReturn)
        {
            if (pointer.Equals(mousePointer))
            {
                cancelPointer(mousePointer);
                if (shouldReturn) mousePointer = internalReturnPointer(mousePointer);
                else mousePointer = internalAddPointer(mousePointer.Position); // can't totally cancel mouse pointer
                return true;
            }
            if (pointer.Equals(fakeMousePointer))
            {
                cancelPointer(fakeMousePointer);
                if (shouldReturn) fakeMousePointer = internalReturnPointer(fakeMousePointer);
                else fakeMousePointer = null;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Releases resources.
        /// </summary>
        public void Dispose()
        {
            if (mousePointer != null)
            {
                cancelPointer(mousePointer);
                mousePointer = null;
            }
            if (fakeMousePointer != null)
            {
                cancelPointer(fakeMousePointer);
                fakeMousePointer = null;
            }
        }

        #endregion

        #region Internal methods

        /// <inheritdoc />
        public void INTERNAL_DiscardPointer(Pointer pointer)
        {
            var p = pointer as MousePointer;
            if (p == null) return;

            mousePool.Release(p);
        }

        #endregion

        #region Private functions

        private Pointer.PointerButtonState getMouseButtons()
        {
            Pointer.PointerButtonState buttons = Pointer.PointerButtonState.Nothing;

            if (Input.GetMouseButton(0)) buttons |= Pointer.PointerButtonState.FirstButtonPressed;
            if (Input.GetMouseButtonDown(0)) buttons |= Pointer.PointerButtonState.FirstButtonDown;
            if (Input.GetMouseButtonUp(0)) buttons |= Pointer.PointerButtonState.FirstButtonUp;

            if (Input.GetMouseButton(1)) buttons |= Pointer.PointerButtonState.SecondButtonPressed;
            if (Input.GetMouseButtonDown(1)) buttons |= Pointer.PointerButtonState.SecondButtonDown;
            if (Input.GetMouseButtonUp(1)) buttons |= Pointer.PointerButtonState.SecondButtonUp;

            if (Input.GetMouseButton(2)) buttons |= Pointer.PointerButtonState.ThirdButtonPressed;
            if (Input.GetMouseButtonDown(2)) buttons |= Pointer.PointerButtonState.ThirdButtonDown;
            if (Input.GetMouseButtonUp(2)) buttons |= Pointer.PointerButtonState.ThirdButtonUp;

            return buttons;
        }

        private void updateButtons(Pointer.PointerButtonState oldButtons, Pointer.PointerButtonState newButtons)
        {
            // pressed something
            if (oldButtons == Pointer.PointerButtonState.Nothing)
            {
                // pressed and released this frame
                if ((newButtons & Pointer.PointerButtonState.AnyButtonPressed) == 0)
                {
                    // Add pressed buttons for processing
                    mousePointer.Buttons = PointerUtils.PressDownButtons(newButtons);
                    pressPointer(mousePointer);
                    internalReleaseMousePointer(newButtons);
                }
                // pressed this frame
                else
                {
                    mousePointer.Buttons = newButtons;
                    pressPointer(mousePointer);
                }
            }
            // released or button state changed
            else
            {
                // released this frame
                if ((newButtons & Pointer.PointerButtonState.AnyButtonPressed) == 0)
                {
                    mousePointer.Buttons = newButtons;
                    internalReleaseMousePointer(newButtons);
                }
                // button state changed this frame
                else
                {
                    mousePointer.Buttons = newButtons;
                    updatePointer(mousePointer);
                }
            }
        }

        private bool fakeTouchReleased()
        {
            if (!CheckValidKeyState())
            {
                // Alt is released, need to kill the fake touch
                fakeGesture = FakeCursorGesture.Null;
                fakeMousePointer.Buttons = PointerUtils.UpPressedButtons(fakeMousePointer.Buttons); // Convert current pressed buttons to UP
                releasePointer(fakeMousePointer);
                removePointer(fakeMousePointer);
                fakeMousePointer = null; // Will be returned to the pool by INTERNAL_DiscardPointer
                return true;
            }
            return false;
        }

        private MousePointer internalAddPointer(Vector2 position, Pointer.PointerButtonState buttons = Pointer.PointerButtonState.Nothing, uint flags = 0)
        {
            var pointer = mousePool.Get();
            pointer.Position = position;
            pointer.Buttons |= buttons;
            pointer.Flags |= flags;
            addPointer(pointer);
            updatePointer(pointer);
            return pointer;
        }

        private void internalReleaseMousePointer(Pointer.PointerButtonState buttons)
        {
            mousePointer.Flags &= ~Pointer.FLAG_RETURNED;
            releasePointer(mousePointer);
        }

        private MousePointer internalReturnPointer(MousePointer pointer)
        {
            var newPointer = mousePool.Get();
            newPointer.CopyFrom(pointer);
            newPointer.Flags |= Pointer.FLAG_RETURNED;
            addPointer(newPointer);
            if ((newPointer.Buttons & Pointer.PointerButtonState.AnyButtonPressed) != 0)
            {
                // Adding down state this frame
                newPointer.Buttons = PointerUtils.DownPressedButtons(newPointer.Buttons);
                pressPointer(newPointer);
            }
            return newPointer;
        }

        private Vector2 remapCoordinates(Vector2 position)
        {
            if (CoordinatesRemapper != null) return CoordinatesRemapper.Remap(position);
            return position;
        }

        private void resetPointer(Pointer p)
        {
            p.INTERNAL_Reset();
        }

        #endregion

        #region State logic

        private void stateMouse()
        {
            setState(State.Mouse);
        }

        private void stateWaitingForFake()
        {
            setState(State.WaitingForFake);
        }

        private void stateMouseAndFake()
        {
            setState(State.MouseAndFake);
        }

        private void stateStationaryFake()
        {
            setState(State.StationaryFake);
        }

        private void setState(State newState)
        {
            state = newState;
        }

        #endregion
    }
}